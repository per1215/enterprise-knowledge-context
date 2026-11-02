# POC demo runbook (2017 MacBook Pro, macOS 13, 16 GB)

A 10–12 minute demo of EKC on this laptop. Everything except the AI assistant runs locally, from source code, in seconds.

## The day before

```bash
cd ~/Documents/SDLC/enterprise-knowledge-context
scripts/demo-setup.sh          # ~1.5 min: build, 47 tests, install `ekc`, fetch eShop, maps, diagram pack, PR repo
scripts/demo.sh --auto         # full rehearsal, ~20 s without pauses
```

Then:

1. **Approve the MCP servers once:** run `claude` in the repo folder, accept `ekc-eshop` and `ekc-fabrikam`, and ask one test question (below).
2. **Check the diagrams:** open `demo/out/DEMO.md` in VS Code → *Markdown: Open Preview*. The Mermaid extension is already installed.
3. **Record a fallback video:** `scripts/demo.sh` plus one Claude Code question (QuickTime → New Screen Recording).
4. **Terminal setup:** font size 18+, dark theme, window about 120×35.

## On the day

- Close Chrome, Docker and other heavy apps. EKC uses under 1 GB; 16 GB is plenty.
- Turn on Do Not Disturb. Have the HDMI/USB-C adapter and charger ready.
- **Wi-Fi:** keep it **off** for scenes 1–7 to prove nothing leaves the laptop. Turn it on only for scene 8, because Claude is a cloud model.

## Script

| # | Time | Do | Say |
|---|---|---|---|
| — | 0:30 | (slide or verbal) | "Your AI assistants and new joiners don't know how *your* system works. When something changes, nobody is sure what breaks. EKC builds a map of the real system from code, with evidence." |
| 1 | 1:00 | `scripts/demo.sh`, scene 1 | "This is Microsoft's eShop: 19 projects. Mapped in seconds from source: no build, no database, no GPU, on a 2017 laptop with Wi-Fi off." |
| 2 | 1:00 | scene 2: `describe ordering-api` | "Everything about one service: endpoints, who calls it, events in and out, tables. Every line points to a file and line." |
| 3 | 2:00 | scene 3: order saga | "This is the whole order process across four services: grace period, stock check in Catalog, payment, then status fan-out. Nobody drew this; it came from the code." Then switch to `DEMO.md` preview, diagram 5. |
| 4 | 1:30 | scene 4: impact | "One method changes in the Order aggregate. Three other services are affected through an event. That's the conversation you want *before* release." |
| 5 | 1:30 | scene 5: Fabrikam | "Business rules and owners: this rule is enforced here, and these two teams need to know." |
| 6 | 1:30 | scene 6: PR | "This comment lands on every pull request automatically. High risk can block the merge." |
| 7 | 0:30 | scene 7 + `DEMO.md` diagram 1/4 | "Architecture diagrams generated on every merge, so they never go stale. Auditors like that." |
| 8 | 2:00 | Wi-Fi on → `claude` | Ask the question below. "The same knowledge inside the assistant your developers already use, through MCP. It works with Copilot, Cursor and Continue too." |

**Scene 8 questions (Claude Code):**

> Using ekc-eshop, how does placing an order flow through the system, and what would break if I changed how OrderStatusChangedToPaid is published?

> Using ekc-fabrikam, who should review a change to ClaimValidator, and why?

Close with: *"Where would this have prevented your last incident?"* Then move into discovery questions about their estate.

## Likely questions

| Question | Answer |
|---|---|
| Does our code leave the building? | No. Mapping, flows and impact are local; the MCP server is local. Only the AI assistant you choose may be cloud-based, and a local model works too. |
| How accurate is it? | Every link has evidence and a confidence score. Calls built from runtime configuration are flagged, not guessed. We'll measure precision and recall on your code in a 2-week trial. |
| Which languages? | .NET today (Aspire, Compose, ASP.NET, gRPC, EF Core, MediatR-style, MassTransit-style). Java next. |
| How is this different from Copilot, Sourcegraph or CAST? | Copilot doesn't know your system. Sourcegraph maps code. CAST is closed and expensive. EKC links business process → service → API → event → table → code, is open source, and runs self-hosted. |
| What does it need from us? | Read access to the repositories, optionally CODEOWNERS and a short overlay file for business processes. |

## If something goes wrong

| Problem | Fix |
|---|---|
| `ekc: command not found` | `export PATH="$HOME/.dotnet/tools:$PATH"` |
| Maps missing or stale | `scripts/demo-setup.sh --skip-tests` |
| Claude Code doesn't show the ekc tools | Run `claude` from the repo root and approve the servers; check with `claude mcp list` |
| Mermaid doesn't render | VS Code → *Markdown: Open Preview* (the `bierner.markdown-mermaid` extension is installed) |
| Anything else | Play the recorded video and talk over it |

## Notes for this laptop

- VS Code here is version 1.77 (2023), which has no MCP support, so use **Claude Code** for scene 8. To use Copilot agent mode instead, update VS Code; `.vscode/mcp.json` is already in the repo.
- A fully offline AI assistant on this Mac is possible but slow: Colima plus Ollama in a container with a 1.5B model (see `../LocalAIBrain`). Avoid it in a live demo; mention that the offline version runs on a 24 GB Apple Silicon laptop.
