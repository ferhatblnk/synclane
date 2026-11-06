# synclane

Turns a curl that works into a SyncLane recipe, and tests the recipe without a database.

```bash
dotnet tool install --global SyncLane.Cli
```

## From a curl to a recipe

Copy a request from the API's documentation or your browser and paste it after `synclane init`:

```bash
synclane init curl "https://api.example-shop.com/v1/orders?updated_since=2026-10-01" \
  -H "Authorization: Bearer $SHOP_TOKEN"
```

`init` sends the request once and writes `orders.jsonc` from the answer:
- where the records are;
- how the API pages;
- which parameter takes the time window;
- which headers say how many requests are left.

Every guess carries a comment that says why. The token in the curl is never written: the recipe names a credential instead.

## Testing a recipe

```bash
synclane test orders.jsonc
```

`test` runs the recipe for two pages through the same code your application uses, with no database. It reports:
- the fields it found;
- whether the record key is unique;
- whether page 2 brought new records.

It never prints record values, cursors or credentials. It exits with 1 when the recipe does not work, so it can run in CI.

Credentials come from environment variables such as `SYNCLANE_EXAMPLE_SHOP_TOKEN`, or from a prompt that does not echo.

## With an AI assistant

`synclane mcp` serves `init`, `test` and a recipe check to an AI assistant over the Model Context Protocol, with the recipe guide, the JSON Schema and the error catalog. Add it to the assistant's MCP settings:

```json
{ "mcpServers": { "synclane": { "command": "synclane", "args": ["mcp"] } } }
```

It runs on your machine with your own assistant: no AI service is called and no port is opened. The tools work only in the folder the server starts in (or `--folder`), and return what the commands print: structure, never data or credentials. Write a token as `$NAME` in the curl and set the variable in the server's environment, so it never enters the conversation. Setup for several assistants: [docs/ai.md](https://github.com/ferhatblnk/synclane/blob/main/docs/ai.md).

## More

- Recipes, credentials and every option: [docs/recipes.md](https://github.com/ferhatblnk/synclane/blob/main/docs/recipes.md)
- For AI assistants: [llms.txt](https://github.com/ferhatblnk/synclane/blob/main/llms.txt)
- Error codes `SL6xxx`: [docs/errors.md](https://github.com/ferhatblnk/synclane/blob/main/docs/errors.md#the-synclane-command)
- The library the recipes run in: [SyncLane](https://www.nuget.org/packages/SyncLane)
