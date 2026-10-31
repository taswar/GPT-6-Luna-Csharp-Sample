using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

// Pick the example from the command line: dotnet run -- 1, 2, 3, 4 or all. An optional second number limits the tickets.
var choice = (args.Length > 0 ? args[0] : "all").TrimStart('-').ToLowerInvariant();
int? limit = args.Length > 1 && int.TryParse(args[1], out var n) && n > 0 ? n : null;
if (choice is not ("1" or "2" or "3" or "4" or "all"))
{
    Console.WriteLine("""
        Usage: dotnet run -- [1 | 2 | 3 | 4 | all] [number of tickets]
          1    Triage support tickets on Luna: accuracy, missed escalations, latency and cost (tickets.json)
          2    Run the invoice agent on Luna and the escalation model, from a PDF and a PNG (invoice.pdf, invoice.png)
          3    Triage on Luna, escalate the hard tickets to the bigger model for a drafted reply
          4    Triage the same tickets at reasoning effort none, low and medium
          all  Run all four (the default)
        """);
    return 1;
}

var config = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddEnvironmentVariables()
    .Build();

var endpoint = (config["AZURE_OPENAI_ENDPOINT"]
    ?? throw new InvalidOperationException(
        "Set AZURE_OPENAI_ENDPOINT with dotnet user-secrets, for example https://YOUR-RESOURCE-NAME.openai.azure.com"))
    .TrimEnd('/');
var luna = config["AZURE_OPENAI_DEPLOYMENT"] ?? "gpt-6-luna";

// Optional: a bigger model on the same resource, such as GPT-6.1 Terra, for examples 2 and 3.
var escalation = config["AZURE_OPENAI_ESCALATION_DEPLOYMENT"];

// Reasoning effort for examples 1 to 3. Example 4 compares the values.
var effort = config["AZURE_OPENAI_REASONING_EFFORT"] ?? "low";

// The API key is optional. Without one, the app signs in with Entra ID through az login.
var apiKey = config["AZURE_OPENAI_API_KEY"];

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
var api = new ResponsesApi(http, endpoint, apiKey, new DefaultAzureCredential());

if (choice is "1" or "all")
{
    await TriageExample.RunAsync(api, luna, effort, limit, cts.Token);
}

if (choice is "2" or "all")
{
    await InvoiceComparisonExample.RunAsync(api, luna, escalation, effort, cts.Token);
}

if (choice is "3" or "all")
{
    await EscalationExample.RunAsync(api, luna, escalation, effort, limit ?? 20, cts.Token);
}

if (choice is "4" or "all")
{
    await EffortSweepExample.RunAsync(api, luna, limit ?? 20, cts.Token);
}

return 0;

// Example 1: high-volume triage. One request per ticket, a long shared prefix that the service caches.
static class TriageExample
{
    public static async Task RunAsync(ResponsesApi api, string deployment, string effort, int? limit, CancellationToken cancellationToken)
    {
        var tickets = Ticket.Load(limit);
        Console.WriteLine($"=== Example 1: triage {tickets.Count} tickets on {deployment} (effort {effort}) ===");

        var run = await TriageAsync(api, deployment, effort, tickets, cancellationToken);
        var results = run.Results;

        // Precision: of the tickets Luna put in a category, how many belong there. Recall: of the tickets that belong, how many it found.
        Console.WriteLine();
        Console.WriteLine($"{"Category",-16} {"Expected",8} {"Predicted",9} {"Precision",9} {"Recall",7} {"F1",5}");
        var f1Scores = new List<double>();
        foreach (var category in results.Select(r => r.Ticket.ExpectedCategory).Distinct().Order())
        {
            var truePositives = results.Count(r => r.Ticket.ExpectedCategory == category && r.Triage.Category == category);
            var predicted = results.Count(r => r.Triage.Category == category);
            var expected = results.Count(r => r.Ticket.ExpectedCategory == category);
            var precision = predicted == 0 ? 0 : (double)truePositives / predicted;
            var recall = (double)truePositives / expected;
            var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
            f1Scores.Add(f1);
            Console.WriteLine($"{category,-16} {expected,8} {predicted,9} {precision,9:0.00} {recall,7:0.00} {f1,5:0.00}");
        }

        var correct = results.Count(r => r.Correct);
        Console.WriteLine($"Accuracy: {correct}/{results.Count}, macro F1 {f1Scores.Average():0.00}");
        foreach (var miss in results.Where(r => !r.Correct).Take(10))
        {
            Console.WriteLine($"  {miss.Ticket.Id}: expected {miss.Ticket.ExpectedCategory}, got {miss.Triage.Category} ({miss.Triage.Confidence}) \"{miss.Ticket.Subject}\"");
        }

        // A missed escalation is a ticket a person should see that Luna handled alone. It's the costly mistake.
        var missed = results.Where(r => r.Ticket.ExpectedEscalate && !r.Escalate).ToList();
        Console.WriteLine();
        Console.WriteLine($"Escalated {results.Count(r => r.Escalate)}, expected {results.Count(r => r.Ticket.ExpectedEscalate)}: " +
            $"missed {missed.Count}, extra {results.Count(r => r.Escalate && !r.Ticket.ExpectedEscalate)}");
        foreach (var miss in missed.Take(10))
        {
            Console.WriteLine($"  Missed {miss.Ticket.Id}: {miss.Triage.Category}, {miss.Triage.Priority}, {miss.Triage.Confidence} \"{miss.Ticket.Subject}\"");
        }

        Console.WriteLine();
        Console.WriteLine($"Latency per request: p50 {run.Percentile(50):0.0}s, p95 {run.Percentile(95):0.0}s, whole run {run.Elapsed.TotalSeconds:0.0}s");
        Console.WriteLine($"First request:  {run.First}");
        Console.WriteLine($"Other requests: {run.Rest}");

        var total = run.Total;
        Console.WriteLine($"[{deployment}] {total}");
        if (Prices.Cost(deployment, total) is { } cost)
        {
            Console.WriteLine($"Estimated cost: {cost:$0.0000} total, {cost / results.Count * 1000:$0.00} per 1,000 tickets, " +
                $"{(correct == 0 ? 0 : cost / correct * 1000):$0.00} per 1,000 correctly classified tickets");
        }

        Console.WriteLine();
    }

    // Triage every ticket. The first request runs alone so it writes the cache; the rest run 4 at a time and read it.
    public static async Task<TriageRun> TriageAsync(
        ResponsesApi api, string deployment, string effort, List<Ticket> tickets, CancellationToken cancellationToken)
    {
        var policy = await File.ReadAllTextAsync("triage-policy.md", cancellationToken);
        var first = new Usage();
        var rest = new Usage();
        var results = new TriageResult[tickets.Count];

        async Task TriageOneAsync(int index, Usage usage)
        {
            var ticket = tickets[index];
            var body = new JsonObject
            {
                ["model"] = deployment,
                // The policy is the same for every ticket, so it forms the cached prefix with the schema.
                ["instructions"] = policy,
                ["prompt_cache_key"] = "contoso-triage-policy-v1",
                ["input"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "input_text", ["text"] = ticket.ToPrompt() }
                        }
                    }
                },
                // Nothing chains off these responses, so don't store them.
                ["store"] = false,
                ["reasoning"] = new JsonObject { ["effort"] = effort },
                ["text"] = new JsonObject { ["format"] = TicketTriageFormat(), ["verbosity"] = "low" }
            };

            var stopwatch = Stopwatch.StartNew();
            using var response = await api.PostAsync(body, cancellationToken);
            stopwatch.Stop();
            usage.Add(response.RootElement);
            var triage = JsonSerializer.Deserialize<TicketTriage>(ResponsesApi.ReadOutputText(response.RootElement), Json.SnakeCase)
                ?? throw new InvalidOperationException($"No triage for {ticket.Id}");
            results[index] = new TriageResult(ticket, triage, stopwatch.Elapsed);
        }

        var run = Stopwatch.StartNew();
        await TriageOneAsync(0, first);

        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(Enumerable.Range(1, tickets.Count - 1).Select(async index =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await TriageOneAsync(index, rest);
            }
            finally
            {
                gate.Release();
            }
        }));

        return new TriageRun(results.ToList(), first, rest, run.Elapsed);
    }

    static JsonObject TicketTriageFormat() => new()
    {
        ["type"] = "json_schema",
        ["name"] = "ticket_triage",
        ["strict"] = true,
        ["schema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["category"] = Enum("billing", "sync", "sign_in", "performance", "feature_request", "data_loss", "other"),
                ["priority"] = Enum("p1", "p2", "p3", "p4"),
                ["needs_human"] = new JsonObject { ["type"] = "boolean" },
                ["confidence"] = Enum("high", "medium", "low"),
                ["summary"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray("category", "priority", "needs_human", "confidence", "summary"),
            ["additionalProperties"] = false
        }
    };

    static JsonObject Enum(params string[] values) => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray())
    };
}

// The escalation rule lives in code, not only in the model's own flags: data loss and p1 tickets always go to a person,
// whatever Luna says about its confidence or needs_human.
static class Routing
{
    public static bool ShouldEscalate(TicketTriage triage) =>
        triage.NeedsHuman
        || triage.Confidence == "low"
        || triage.Category == "data_loss"
        || triage.Priority == "p1";
}

// Example 2: the GPT-6.1 Terra invoice agent, unchanged, on Luna and on the escalation model, from a PDF and from a PNG.
static class InvoiceComparisonExample
{
    static readonly (string Name, Func<InvoiceReview, object> Get)[] Fields =
    [
        ("Supplier", r => r.SupplierName),
        ("On file", r => r.SupplierOnFile),
        ("Invoice number", r => r.InvoiceNumber),
        ("Invoice date", r => r.InvoiceDate),
        ("PO number", r => r.PoNumber),
        ("Currency", r => r.Currency),
        ("Total", r => r.Total),
        ("Needs approval", r => r.RequiresApproval),
        ("Issues", r => r.Issues.Count)
    ];

    public static async Task RunAsync(ResponsesApi api, string luna, string? escalation, string effort, CancellationToken cancellationToken)
    {
        Console.WriteLine($"=== Example 2: the invoice agent on {luna}{(escalation is null ? "" : $" and {escalation}")}, from a PDF and a PNG ===");

        var plans = new List<(string Label, string Deployment, string File)>
        {
            ("Luna PDF", luna, "invoice.pdf"),
            ("Luna PNG", luna, "invoice.png")
        };
        if (escalation is not null)
        {
            var name = escalation.Length <= 14 ? escalation : escalation[..14];
            plans.Add(($"{name} PDF", escalation, "invoice.pdf"));
            plans.Add(($"{name} PNG", escalation, "invoice.png"));
        }

        var runs = new List<(string Label, string Deployment, InvoiceRun Run)>();
        foreach (var (label, deployment, file) in plans)
        {
            runs.Add((label, deployment, await InvoiceAgent.RunAsync(api, deployment, effort, file, cancellationToken)));
        }

        // The bigger model reading the PDF is the reference when there is one; otherwise Luna reading the PDF.
        var reference = runs.Count > 2 ? runs[2] : runs[0];

        Console.WriteLine();
        Console.WriteLine($"{"Field",-16}" + string.Concat(runs.Select(r => $" {r.Label,-25}")));
        foreach (var (name, get) in Fields)
        {
            Console.WriteLine($"{name,-16}" + string.Concat(runs.Select(r => $" {Short(get(r.Run.Review).ToString()),-25}")));
        }

        Console.WriteLine($"{"Matches ref.",-16}" + string.Concat(runs.Select(r =>
            $" {Fields.Count(f => Equals(f.Get(r.Run.Review), f.Get(reference.Run.Review))) + "/" + Fields.Length,-25}")));
        Console.WriteLine($"(The reference is {reference.Label}.)");

        Console.WriteLine();
        foreach (var (label, deployment, run) in runs)
        {
            foreach (var issue in run.Review.Issues)
            {
                Console.WriteLine($"  {label} issue: {issue}");
            }

            var cost = Prices.Cost(deployment, run.Usage);
            Console.WriteLine($"[{label}: {deployment}] {run.Usage} elapsed={run.Elapsed.TotalSeconds:0.0}s" +
                (cost is null ? "" : $" cost={cost:$0.0000} ({cost * 1000:$0.00} per 1,000 invoices)"));
        }

        if (escalation is null)
        {
            Console.WriteLine("Set AZURE_OPENAI_ESCALATION_DEPLOYMENT to compare with a bigger model.");
        }

        Console.WriteLine();
    }

    static string Short(string? value) => value is null ? "" : value.Length <= 24 ? value : value[..23] + "…";
}

// Example 3: Luna triages everything; only the tickets the rule escalates go to the bigger model.
static class EscalationExample
{
    const string DraftInstructions = """
        You are a senior support agent for Contoso Sync Pro, a file sync service.
        Draft a reply to the customer in under 120 words. Be specific to their ticket and say what happens next.
        Don't promise refunds, restores or fix dates, and don't ask for passwords.
        """;

    public static async Task RunAsync(ResponsesApi api, string luna, string? escalation, string effort, int count, CancellationToken cancellationToken)
    {
        Console.WriteLine($"=== Example 3: triage {count} tickets on {luna}, escalate the hard ones ===");
        if (escalation is null)
        {
            Console.WriteLine("Skipped. Set AZURE_OPENAI_ESCALATION_DEPLOYMENT to a bigger model, such as your GPT-6.1 Terra deployment.");
            Console.WriteLine();
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var run = await TriageExample.TriageAsync(api, luna, effort, Ticket.Load(count), cancellationToken);
        var escalated = run.Results.Where(r => r.Escalate).ToList();

        var draftUsage = new Usage();
        foreach (var result in escalated)
        {
            var (ticket, triage) = (result.Ticket, result.Triage);
            var body = new JsonObject
            {
                ["model"] = escalation,
                ["instructions"] = DraftInstructions,
                ["input"] = $"{ticket.ToPrompt()}\n\nTriage from the first-line model: {triage.Category}, {triage.Priority}. {triage.Summary}",
                ["store"] = false,
                ["reasoning"] = new JsonObject { ["effort"] = effort },
                ["text"] = new JsonObject { ["verbosity"] = "low" }
            };

            using var response = await api.PostAsync(body, cancellationToken);
            draftUsage.Add(response.RootElement);
            var draft = ResponsesApi.ReadOutputText(response.RootElement).ReplaceLineEndings(" ");
            Console.WriteLine($"{ticket.Id} [{triage.Category}, {triage.Priority}, {triage.Confidence}] {ticket.Subject}");
            Console.WriteLine($"  Draft: {(draft.Length <= 160 ? draft : draft[..160] + "…")}");
        }

        stopwatch.Stop();

        var missed = run.Results.Count(r => r.Ticket.ExpectedEscalate && !r.Escalate);
        Console.WriteLine();
        Console.WriteLine($"Escalated {escalated.Count} of {run.Results.Count} tickets ({100.0 * escalated.Count / run.Results.Count:0}%); " +
            $"{missed} that should have been escalated weren't.");
        Console.WriteLine($"[{luna}] {run.Total}");
        Console.WriteLine($"[{escalation}] {draftUsage}");
        Console.WriteLine($"elapsed={stopwatch.Elapsed.TotalSeconds:0.0}s");

        var lunaCost = Prices.Cost(luna, run.Total);
        var draftCost = Prices.Cost(escalation, draftUsage);
        if (lunaCost is not null && draftCost is not null && escalated.Count > 0)
        {
            // An estimate: the average measured draft cost, applied to every ticket.
            var allOnBigModel = draftCost.Value / escalated.Count * run.Results.Count;
            Console.WriteLine($"Estimated cost: {lunaCost + draftCost:$0.0000} for this run, against about {allOnBigModel:$0.0000} " +
                $"to draft a reply for every ticket on {escalation}.");
        }

        Console.WriteLine();
    }
}

// Example 4: the same tickets at each reasoning effort. A deployment that rejects a value shows it instead of failing.
static class EffortSweepExample
{
    public static async Task RunAsync(ResponsesApi api, string deployment, int count, CancellationToken cancellationToken)
    {
        var tickets = Ticket.Load(count);
        Console.WriteLine($"=== Example 4: {tickets.Count} tickets at each reasoning effort on {deployment} ===");
        Console.WriteLine($"{"Effort",-8} {"Accuracy",9} {"Missed esc.",12} {"Reasoning",10} {"Cost/1k",8} {"p50",6} {"p95",6}");

        foreach (var effort in new[] { "none", "low", "medium" })
        {
            try
            {
                var run = await TriageExample.TriageAsync(api, deployment, effort, tickets, cancellationToken);
                var correct = run.Results.Count(r => r.Correct);
                var missed = run.Results.Count(r => r.Ticket.ExpectedEscalate && !r.Escalate);
                var cost = Prices.Cost(deployment, run.Total);
                Console.WriteLine($"{effort,-8} {$"{correct}/{run.Results.Count}",9} {missed,12} {run.Total.Reasoning,10} " +
                    $"{(cost is null ? "n/a" : (cost.Value / run.Results.Count * 1000).ToString("$0.00")),8} " +
                    $"{run.Percentile(50),5:0.0}s {run.Percentile(95),5:0.0}s");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("returned 400"))
            {
                Console.WriteLine($"{effort,-8} not accepted by this deployment: {ex.Message[..Math.Min(120, ex.Message.Length)]}");
            }
        }

        Console.WriteLine();
    }
}

// The invoice agent from the GPT-6.1 Terra sample: a PDF or image, one tool call, structured output.
static class InvoiceAgent
{
    const string Instructions = """
        You review supplier invoices for accounts payable.
        Read the attached invoice. Always call check_supplier with the supplier name exactly as printed.
        Set requires_approval to true if the total is over 5000, or if the supplier is not on file or not active.
        List anything a reviewer should look at in issues, such as math that doesn't add up or a missing PO number.
        Use an empty list when there is nothing to flag.
        """;

    public static async Task<InvoiceRun> RunAsync(
        ResponsesApi api, string deployment, string effort, string path, CancellationToken cancellationToken)
    {
        var base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(path, cancellationToken));

        // A PDF goes in as input_file; an image as input_image, with high detail so the small print is readable.
        JsonObject document = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? new JsonObject
            {
                ["type"] = "input_file",
                ["filename"] = Path.GetFileName(path),
                ["file_data"] = $"data:application/pdf;base64,{base64}"
            }
            : new JsonObject
            {
                ["type"] = "input_image",
                ["image_url"] = $"data:image/png;base64,{base64}",
                ["detail"] = "high"
            };

        var body = new JsonObject
        {
            ["model"] = deployment,
            ["instructions"] = Instructions,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        document,
                        new JsonObject { ["type"] = "input_text", ["text"] = "Review this invoice." }
                    }
                }
            }
        };
        AddToolsAndFormat(body, effort);

        var stopwatch = Stopwatch.StartNew();
        var usage = new Usage();
        var response = await api.PostAsync(body, cancellationToken);
        usage.Add(response.RootElement);

        // The tool loop: the model asks for check_supplier, this code runs it and sends the result back.
        // previous_response_id chains the turns; the loop stops when the model asks for no more calls.
        for (var round = 1; round <= 5; round++)
        {
            var outputs = RunToolCalls(response.RootElement, deployment);
            if (outputs.Count == 0)
            {
                break;
            }

            var next = new JsonObject
            {
                ["model"] = deployment,
                ["previous_response_id"] = response.RootElement.GetProperty("id").GetString(),
                ["input"] = outputs
            };
            AddToolsAndFormat(next, effort);

            response.Dispose();
            response = await api.PostAsync(next, cancellationToken);
            usage.Add(response.RootElement);
        }

        stopwatch.Stop();
        var json = ResponsesApi.ReadOutputText(response.RootElement);
        response.Dispose();

        var review = JsonSerializer.Deserialize<InvoiceReview>(json, Json.SnakeCase)
            ?? throw new InvalidOperationException($"{deployment} returned no invoice review: {json}");
        return new InvoiceRun(review, usage, stopwatch.Elapsed);
    }

    static JsonArray RunToolCalls(JsonElement root, string deployment)
    {
        var outputs = new JsonArray();
        foreach (var call in root.GetProperty("output").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "function_call"))
        {
            var arguments = JsonNode.Parse(call.GetProperty("arguments").GetString() ?? "{}")!;
            var name = arguments["supplier_name"]?.GetValue<string>() ?? "";
            var result = CheckSupplier(name);
            Console.WriteLine($"[{deployment}] check_supplier(\"{name}\") -> {result}");

            outputs.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = call.GetProperty("call_id").GetString(),
                ["output"] = result
            });
        }

        return outputs;
    }

    // A stand-in for your ERP or vendor master.
    static string CheckSupplier(string supplierName)
    {
        var suppliers = new Dictionary<string, (string Id, string Status)>(StringComparer.OrdinalIgnoreCase)
        {
            ["Fabrikam Office Supplies"] = ("V-1042", "active"),
            ["Northwind Traders"] = ("V-0877", "on hold")
        };

        var result = suppliers.TryGetValue(supplierName.Trim(), out var supplier)
            ? new JsonObject { ["found"] = true, ["supplier_id"] = supplier.Id, ["status"] = supplier.Status }
            : new JsonObject { ["found"] = false };
        return result.ToJsonString();
    }

    static void AddToolsAndFormat(JsonObject body, string effort)
    {
        body["tools"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "function",
                ["name"] = "check_supplier",
                ["description"] = "Looks up a supplier in the vendor master. Returns whether it exists, its ID and its status.",
                ["strict"] = true,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["supplier_name"] = new JsonObject { ["type"] = "string", ["description"] = "The supplier name as printed on the invoice." }
                    },
                    ["required"] = new JsonArray("supplier_name"),
                    ["additionalProperties"] = false
                }
            }
        };
        body["reasoning"] = new JsonObject { ["effort"] = effort };
        body["text"] = new JsonObject
        {
            ["verbosity"] = "low",
            ["format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["name"] = "invoice_review",
                ["strict"] = true,
                ["schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["supplier_name"] = new JsonObject { ["type"] = "string" },
                        ["supplier_on_file"] = new JsonObject { ["type"] = "boolean" },
                        ["invoice_number"] = new JsonObject { ["type"] = "string" },
                        ["invoice_date"] = new JsonObject { ["type"] = "string", ["description"] = "ISO 8601 date, for example 2026-10-12." },
                        ["po_number"] = new JsonObject { ["type"] = "string", ["description"] = "Empty if the invoice has no PO number." },
                        ["currency"] = new JsonObject { ["type"] = "string", ["description"] = "ISO 4217 code, for example USD." },
                        ["total"] = new JsonObject { ["type"] = "number" },
                        ["requires_approval"] = new JsonObject { ["type"] = "boolean" },
                        ["issues"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
                    },
                    ["required"] = new JsonArray(
                        "supplier_name", "supplier_on_file", "invoice_number", "invoice_date",
                        "po_number", "currency", "total", "requires_approval", "issues"),
                    ["additionalProperties"] = false
                }
            }
        };
    }
}

record Ticket(string Id, string From, string Subject, string Body, string ExpectedCategory, bool ExpectedEscalate)
{
    public static List<Ticket> Load(int? limit)
    {
        var tickets = JsonSerializer.Deserialize<List<Ticket>>(File.ReadAllText("tickets.json"), Json.SnakeCase)
            ?? throw new InvalidOperationException("tickets.json is empty.");
        return limit is null ? tickets : tickets.Take(limit.Value).ToList();
    }

    // The expected answers stay out of the prompt; they're only for scoring.
    public string ToPrompt() => $"Ticket {Id}\nFrom: {From}\nSubject: {Subject}\n\n{Body}";
}

record TicketTriage(string Category, string Priority, bool NeedsHuman, string Confidence, string Summary);

record TriageResult(Ticket Ticket, TicketTriage Triage, TimeSpan Latency)
{
    public bool Correct => Triage.Category == Ticket.ExpectedCategory;
    public bool Escalate => Routing.ShouldEscalate(Triage);
}

// One triage run: the results, usage for the first request (cache write) and the rest (cache reads), and wall-clock time.
record TriageRun(List<TriageResult> Results, Usage First, Usage Rest, TimeSpan Elapsed)
{
    public Usage Total => Usage.Sum(First, Rest);

    public double Percentile(int percent)
    {
        var sorted = Results.Select(r => r.Latency.TotalSeconds).Order().ToList();
        return sorted[Math.Max(0, (int)Math.Ceiling(percent / 100.0 * sorted.Count) - 1)];
    }
}

record InvoiceReview(
    string SupplierName,
    bool SupplierOnFile,
    string InvoiceNumber,
    string InvoiceDate,
    string PoNumber,
    string Currency,
    decimal Total,
    bool RequiresApproval,
    List<string> Issues);

record InvoiceRun(InvoiceReview Review, Usage Usage, TimeSpan Elapsed);

static class Json
{
    public static readonly JsonSerializerOptions SnakeCase = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}

// Global Standard short-context prices in USD per million tokens, from Microsoft's announcements. Edit them for your region.
record Price(decimal Input, decimal CachedInput, decimal CacheWrite, decimal Output);

static class Prices
{
    static readonly Dictionary<string, Price> Known = new()
    {
        ["luna"] = new(0.10m, 0.01m, 0.125m, 0.50m),
        ["terra"] = new(1.00m, 0.05m, 1.25m, 5.00m),
        // GPT-6.1 Sol. GPT-6 Sol had the same input and output prices, with $0.20 cached input.
        ["sol"] = new(2.00m, 0.10m, 2.50m, 10.00m),
        ["astra"] = new(10.00m, 1.00m, 12.50m, 50.00m)
    };

    // Matches the price by the deployment name, so name your deployments after the model.
    public static Price? For(string deployment) =>
        Known.FirstOrDefault(p => deployment.Contains(p.Key, StringComparison.OrdinalIgnoreCase)).Value;

    // An estimate. Cached and cache-write tokens are billed at their own rates; the rest of the input at the input rate.
    public static decimal? Cost(string deployment, Usage usage)
    {
        if (For(deployment) is not { } price)
        {
            return null;
        }

        var uncached = Math.Max(0, usage.Input - usage.Cached - usage.CacheWrite);
        return (uncached * price.Input + usage.Cached * price.CachedInput + usage.CacheWrite * price.CacheWrite
            + usage.Output * price.Output) / 1_000_000m;
    }
}

// Sends requests to the Responses API with an API key or an Entra ID token, and retries throttling and server errors.
sealed class ResponsesApi(HttpClient http, string endpoint, string? apiKey, TokenCredential credential)
{
    public async Task<JsonDocument> PostAsync(JsonObject body, CancellationToken cancellationToken)
    {
        var payload = body.ToJsonString();
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/openai/v1/responses");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Add("api-key", apiKey);
            }
            else
            {
                var token = await credential.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            }

            // StringContent sets a fixed Content-Length; JsonContent uses chunked encoding.
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            TimeSpan delay;
            try
            {
                using var response = await http.SendAsync(request, cancellationToken);
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return JsonDocument.Parse(text);
                }

                var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
                if (!retryable || attempt == 5)
                {
                    throw new InvalidOperationException($"Responses API returned {(int)response.StatusCode}: {text}");
                }

                // Wait as long as the service asks, or back off: 2, 4, 8, 16 seconds.
                delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Console.WriteLine($"[retry] {(int)response.StatusCode}, waiting {delay.TotalSeconds:0}s");
            }
            catch (HttpRequestException ex) when (attempt < 5)
            {
                // A dropped connection. The service may have processed the request already, so a retry can repeat it.
                // That's fine for read-only work like triage; don't retry blindly around tools that change things.
                delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Console.WriteLine($"[retry] {ex.Message}, waiting {delay.TotalSeconds:0}s");
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    public static string ReadOutputText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var convenience) && convenience.ValueKind == JsonValueKind.String)
        {
            return convenience.GetString() ?? string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in root.GetProperty("output").EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var type) || type.GetString() != "message")
            {
                continue;
            }

            foreach (var part in item.GetProperty("content").EnumerateArray())
            {
                if (part.TryGetProperty("type", out var partType) && partType.GetString() == "output_text")
                {
                    builder.Append(part.GetProperty("text").GetString());
                }
            }
        }

        return builder.ToString();
    }
}

// Token usage summed across calls. Thread-safe, because example 1 runs requests in parallel.
sealed class Usage
{
    readonly Lock gate = new();

    public int Calls { get; private set; }
    public int Input { get; private set; }
    public int Cached { get; private set; }
    public int CacheWrite { get; private set; }
    public int Output { get; private set; }
    public int Reasoning { get; private set; }

    public void Add(JsonElement root)
    {
        lock (gate)
        {
            Calls++;
            if (!root.TryGetProperty("usage", out var usage))
            {
                return;
            }

            Input += Read(usage, "input_tokens");
            Output += Read(usage, "output_tokens");
            if (usage.TryGetProperty("input_tokens_details", out var inputDetails))
            {
                Cached += Read(inputDetails, "cached_tokens");
                // Cache writes are reported and billed on GPT-5.6 and later models.
                CacheWrite += Read(inputDetails, "cache_write_tokens");
            }

            if (usage.TryGetProperty("output_tokens_details", out var outputDetails))
            {
                Reasoning += Read(outputDetails, "reasoning_tokens");
            }
        }
    }

    public static Usage Sum(params Usage[] parts) => new()
    {
        Calls = parts.Sum(p => p.Calls),
        Input = parts.Sum(p => p.Input),
        Cached = parts.Sum(p => p.Cached),
        CacheWrite = parts.Sum(p => p.CacheWrite),
        Output = parts.Sum(p => p.Output),
        Reasoning = parts.Sum(p => p.Reasoning)
    };

    public override string ToString() =>
        $"calls={Calls} input={Input} cached={Cached} cache_write={CacheWrite} output={Output} reasoning={Reasoning}";

    static int Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
