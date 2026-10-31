# Contributing

Thanks for helping improve this sample. It's a small .NET 10 console app that uses GPT-6 Luna on Microsoft Foundry, through the Responses API, as the cheap first stage of a high-volume agent. It has four examples: ticket triage with scoring, the invoice agent on Luna and a bigger model, triage with escalation, and a reasoning effort sweep. Contributions that make it clearer, more correct, or more useful to other .NET developers are welcome.

## Ways to contribute

- **Report a bug.** An example fails, or the README doesn't match what the code does.
- **Fix the docs.** Typos, unclear steps, or a missing troubleshooting case.
- **Improve the sample.** For example, a clearer accuracy report, or a better way to show the cache write and cache reads in Example 1.
- **Share results.** If you ran the triage or the effort sweep on your own deployment, an issue with the accuracy, missed escalations, latency and token counts helps others decide whether Luna fits their workload.

For anything larger than a small fix, open an issue first so we can agree on the approach before you write code.

## Set up

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and the Azure CLI.
2. Clone the repo and build it:

   ```bash
   git clone https://github.com/taswar/GPT-6-Luna-Csharp-Sample.git
   cd GPT-6-Luna-Csharp-Sample
   dotnet build
   ```

3. To run it, deploy GPT-6 Luna on a Microsoft Foundry resource, set `AZURE_OPENAI_ENDPOINT` and `AZURE_OPENAI_DEPLOYMENT` with user secrets, and sign in with `az login`, as described in the [README](README.md#run-it). Examples 2 and 3 also need a bigger model in `AZURE_OPENAI_ESCALATION_DEPLOYMENT`. For Entra ID, your account needs the **Cognitive Services OpenAI User** role on the resource. An API key in `AZURE_OPENAI_API_KEY` works too.

While you work, run a handful of tickets, such as `dotnet run -- 1 10`, to keep the cost down.

## Guidelines

- **Never commit secrets.** No keys, endpoints, tenant IDs or tokens in code, config files or screenshots. Settings belong in user secrets or environment variables.
- **Keep Entra ID as the default.** The API key is optional. Don't make it required.
- **Keep the raw Responses API calls.** The sample posts JSON with `HttpClient` on purpose, so readers can see the request shape. Don't swap it for an SDK wrapper or switch to `/chat/completions`.
- **Keep the escalation deployment optional.** Examples 1 and 4 must run with only a Luna deployment. Example 2 falls back to Luna only, and Example 3 skips with a message.
- **Keep the routing rule in code.** `Routing.ShouldEscalate` always escalates data loss, p1 and low-confidence tickets, whatever the model says. Don't make it rely only on `needs_human`.
- **Keep the cached prefix stable.** The policy, the schema and `prompt_cache_key` are the same for every triage request. Anything that varies per ticket goes in `input`, after the prefix. If you change `triage-policy.md` in a meaningful way, bump the cache key.
- **Keep the labels honest.** `tickets.json` is the test set. If you add or change a ticket, set `expected_category` and `expected_escalate` from `triage-policy.md`, not from what the model answered. Never put the expected answers in the prompt.
- **Keep the sample data fake.** Tickets, `invoice.pdf` and `invoice.png` must not contain real people, companies or financial data. Use the Contoso, Fabrikam and Northwind style of fictional names.
- **Keep the tool read-only and simulated.** `check_supplier` only looks up a hard-coded dictionary. Don't add tools that write, pay or send, and don't connect the sample to a real system.
- **Keep schemas and records in sync.** If you change the `ticket_triage` or `invoice_review` schema, update `TicketTriage` or `InvoiceReview` in the same change. Strict mode needs every property in `required` and `additionalProperties` set to `false`.
- **Keep the numbers visible.** Every example prints its usage, and where it can, latency and an estimated cost. Keep that for any new example. If you update `Prices`, say where the new prices come from.
- **Keep it a sample.** The code should stay short and readable in one `Program.cs`. Prefer clarity over abstraction, and add a dependency only when it clearly helps.
- **Match the existing style.** Target `net10.0`, keep nullable reference types on, and follow the layout already in `Program.cs`.
- **Keep the README in sync.** If you add an example, change settings or change output, update the README in the same pull request, including the "What's inside" table and the usage text in `Program.cs`.
- **One change per pull request.** Small, focused pull requests are easier to review and merge.

## Pull request checklist

- [ ] `dotnet build` succeeds with no new warnings.
- [ ] Any example you changed runs against your own deployment, or you explain why you couldn't run it.
- [ ] No secrets, endpoints or personal data are in the diff.
- [ ] Changes to `tickets.json` or `triage-policy.md` keep the labels consistent with the policy.
- [ ] The README reflects any change in examples, settings or output.
- [ ] The pull request description says what changed and why.

## Reporting a bug

Open an issue and include:

- Which example failed (`1`, `2`, `3` or `4`), the ticket limit if you used one, and what you expected.
- The full error message, with any endpoints removed.
- Your .NET SDK version (`dotnet --version`) and operating system.
- Whether you used Entra ID or an API key, and whether the error was a timeout or a `429`.

## Security issues

Please don't report security problems in a public issue. Use **Security → Report a vulnerability** on the repo to send a private report.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE) that covers this project.
