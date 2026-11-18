# Provider API sources

Checked 18 November 2026. Sentinel uses native HTTP requests and shared report parsing; provider keys are never available to the web page.

- OpenAI organization usage and costs: https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage
- Anthropic legacy organization usage and cost reports: https://platform.claude.com/docs/en/manage-claude/usage-cost-api
- Anthropic newer analytics API (not used by this release): https://platform.claude.com/docs/en/manage-claude/usage-cost-analytics-api
- OpenRouter current key: https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key
- OpenRouter account credits: https://openrouter.ai/docs/api/api-reference/credits/get-credits
- DeepSeek balance by currency: https://api-docs.deepseek.com/api/get-user-balance/
- Coarena Model API: https://coarena.ai/docs
- Coarena endpoint schemas: https://coarena.ai/api/openapi.json
- Coarena agent endpoints: https://coarena.ai/for-agents.md
- Ollama generation measurements: https://docs.ollama.com/api/generate
- Ollama installed models: https://docs.ollama.com/api/tags
- Ollama loaded models: https://docs.ollama.com/api/ps
- AgentRouter site: https://agentrouter.org — no verified automatic billing endpoint is assumed.

Directory entries marked dashboard/manual provide a direct provider link and local key management. Their existence does not imply a working billing integration. Names and console routes can change; custom HTTPS services remain available.


## AgentRouter.org compatibility reports (1.1)

Live authenticated requests confirmed `/v1/dashboard/billing/subscription` returns `hard_limit_usd`, and `/v1/dashboard/billing/usage` returns `total_usage`. The former is displayed only as an API-key allowance. No available account-wallet amount is inferred. The latter is shown as a raw counter because neither currency unit nor date range is verified in the provider docs. `/api/usage/token` returned 404. Routes from the unrelated agentrouter.to service are not used.

## OpenRouter account authorization

Official PKCE protocol: https://openrouter.ai/docs/guides/overview/auth/oauth . Windows uses an ephemeral loopback callback with random state and S256. Android uses the documented headless flow and one-time authorization-code entry. Keys are exchanged natively and saved in the device vault. Google/GitHub authentication occurs on the provider site if offered.
