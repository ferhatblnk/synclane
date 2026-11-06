# Writing recipes with an AI assistant

An AI assistant can write a recipe for you from a curl that works. SyncLane gives it two things:

- **[`llms.txt`](../llms.txt)**, at the root of the repository: a short summary and links to the recipe guide, the JSON Schema and the error catalog, in the format assistants read. Point any assistant at it.
- **`synclane mcp`**: the `synclane` command as tools the assistant can call, over the [Model Context Protocol](https://modelcontextprotocol.io/). The assistant drafts a recipe from your curl, checks it, and runs it for two pages, then fixes what the results say.

The assistant runs where it already runs, with your own account. SyncLane calls no AI service, needs no key of ours and opens no port: the assistant starts `synclane mcp` on your machine and talks to it over standard input and output.

## Setting it up

Install the command once:

```bash
dotnet tool install --global SyncLane.Cli
```

Then add it to your assistant's MCP settings. The server is the command `synclane` with the argument `mcp`. For example, in a project's `.mcp.json` (Claude Code), `.cursor/mcp.json` (Cursor) or Claude Desktop's settings:

```json
{
  "mcpServers": {
    "synclane": {
      "command": "synclane",
      "args": ["mcp"]
    }
  }
}
```

In VS Code, `.vscode/mcp.json`:

```json
{
  "servers": {
    "synclane": { "type": "stdio", "command": "synclane", "args": ["mcp"] }
  }
}
```

The tools read and write recipes only in the folder the assistant starts the server in, which is the open project for most editors. To work elsewhere, add `"--folder", "path/to/recipes"` to the arguments.

## The tools

| Tool | What it does | Reaches the network |
| --- | --- | --- |
| `synclane_recipe_guide` | The [recipe guide](recipes.md), for the assistant to read first. | No |
| `synclane_recipe_schema` | The recipe's JSON Schema, with every field described. | No |
| `synclane_init` | `synclane init`: sends your curl once, writes a draft with a comment beside every guess, and returns it. | Yes, once, to the URL in the curl |
| `synclane_check` | Reads a recipe file and names every problem with how to fix it. | No |
| `synclane_test` | `synclane test`: runs the recipe for two pages without a database and reports the records' fields, whether the key is unique and whether paging works. | Yes, to the recipe's URL |
| `synclane_explain_error` | What a code such as `SL5106` means and how to fix it. | No |

The tools run the same code as the commands, so they return what the commands print and nothing more: field names, counts and comments, never a record, a cursor, a URL's key or a credential. Most assistants ask you before they call a tool that reaches the network.

## Keeping credentials out of the conversation

A recipe never holds a credential; it names one, and your application supplies the value. When the assistant drafts and tests a recipe, the request needs the real value, and it should not pass through the conversation, where it would reach the assistant's provider.

So put it in an environment variable of the server, and write `$NAME` in the curl:

```json
{
  "mcpServers": {
    "synclane": {
      "command": "synclane",
      "args": ["mcp"],
      "env": { "SHOP_TOKEN": "${SHOP_TOKEN}" }
    }
  }
}
```

```text
curl -H "Authorization: Bearer $SHOP_TOKEN" "https://api.example-shop.com/v1/orders?updated_since=2026-10-01"
```

`synclane_init` replaces `$SHOP_TOKEN` (or `${SHOP_TOKEN}`) with the variable's value when it sends the request, and the recipe it writes names a credential instead. `synclane_test` reads the credential from the variables named in its error (`SL6007`), such as `SYNCLANE_EXAMPLE_SHOP_TOKEN`; set those the same way. Whether `${SHOP_TOKEN}` in the settings is read from your shell depends on the assistant; where it is not, write the value there, and keep that file out of version control.

If you paste a curl with the token in it into the conversation, the assistant's provider has the token. Revoke it and make a new one.

## Without MCP

Any assistant can write a recipe from [`llms.txt`](../llms.txt) and the guide alone. Run `synclane init` and `synclane test` yourself and paste their output back: it holds no data and no credential, by design, so it is safe to share.
