# mcp-server-for-revit

MCP server for interacting with Autodesk Revit through AI assistants like Claude.

This package is the MCP server component of [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit). It exposes Revit operations as MCP tools that AI clients can call. The server communicates with the [Revit plugin](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit) over WebSocket to execute commands inside Revit.

> [!NOTE]
> This server requires the mcp-servers-for-revit Revit plugin to be installed and running inside Revit. See the [full project README](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit) for setup instructions.

## Setup

**Claude Code**

```bash
claude mcp add mcp-server-for-revit -- npx -y mcp-server-for-revit
```

**Claude Desktop**

Claude Desktop → Settings → Developer → Edit Config → `claude_desktop_config.json`:

```json
{
    "mcpServers": {
        "mcp-server-for-revit": {
            "command": "npx",
            "args": ["-y", "mcp-server-for-revit"]
        }
    }
}
```

Restart Claude Desktop. When you see the hammer icon, the MCP server is connected.

## Tool Catalogs

To keep the MCP client's context window small, the server exposes tools in two layers:

- **Layer 1 (always loaded):** the `core` catalog plus three meta tools, about 3k tokens in total.
  - `search_tools` finds tools by keyword or catalog.
  - `enable_catalog` loads a catalog's tools as native tools.
  - `call_tool` runs a **read-only** catalog tool without loading it.
- **Layer 2 (on demand):** catalogs modelled on the Revit ribbon tabs:

| Catalog | Aliases | Contents |
| ------- | ------- | -------- |
| `architecture` | `arch` | Walls, doors, windows, floors, roofs, rooms, levels, grids |
| `structure` | `struct`, `register` | Framing systems, register extraction (grid, column/wall, beam), shared modelling tools |
| `annotate` | `drafting` | Dimensions, wall and room tags |
| `modify` | `edit` | Select, hide, recolor, transparency, delete |
| `analyze` | `analysis` | Model statistics, material quantities, room data export |
| `data` | `database` | Local SQLite project and room storage |
| `automation` | `code` | Run C# code inside Revit |

Tools that modify the model can only be called after their catalog is enabled, so the client's per-tool permission prompts still apply.

Configure the behaviour with environment variables on the MCP server:

| Variable | Values | Default |
| -------- | ------ | ------- |
| `REVIT_MCP_TOOL_MODE` | `dynamic` (layered) or `all` (expose every tool up front) | `dynamic` |
| `REVIT_MCP_CATALOGS` | Comma-separated catalogs to load at startup, e.g. `structure,drafting` | *(none)* |

`dynamic` mode relies on the client refreshing its tool list when it receives `notifications/tools/list_changed`. If your client does not, read-only tools still work through `call_tool`. For tools that modify the model, set `REVIT_MCP_TOOL_MODE=all` or preload their catalogs with `REVIT_MCP_CATALOGS`.

## Supported Tools

| Tool | Catalog | Description |
| ---- | ------- | ----------- |
| `get_current_view_info` | core | Get current active view info |
| `get_current_view_elements` | core | Get elements from the current active view |
| `get_available_family_types` | core | Get available family types in current project |
| `get_selected_elements` | core | Get currently selected elements |
| `get_material_quantities` | analyze | Calculate material quantities and takeoffs |
| `ai_element_filter` | core | Intelligent element querying tool for AI assistants |
| `analyze_model_statistics` | analyze | Analyze model complexity with element counts |
| `create_point_based_element` | architecture, structure | Create point-based elements (door, window, furniture) |
| `create_line_based_element` | architecture, structure | Create line-based elements (wall, beam, pipe) |
| `create_surface_based_element` | architecture, structure | Create surface-based elements (floor, ceiling, roof) |
| `create_grid` | architecture, structure | Create a grid system with smart spacing generation |
| `create_level` | architecture, structure | Create levels at specified elevations |
| `create_room` | architecture | Create and place rooms at specified locations |
| `create_dimensions` | annotate | Create dimension annotations in the current view |
| `create_structural_framing_system` | structure | Create a structural beam framing system |
| `delete_element` | modify | Delete elements by ID |
| `operate_element` | modify | Operate on elements (select, setColor, hide, etc.) |
| `color_elements` | modify | Color elements based on a parameter value |
| `tag_all_walls` | annotate | Tag all walls in the current view |
| `tag_all_rooms` | annotate | Tag all rooms in the current view |
| `export_room_data` | analyze, architecture | Export all room data from the project |
| `get_grid_register_data` | structure | Return register-ready grid data (one record per grid, axis family and coordinate in mm) |
| `get_column_wall_register_data` | structure | Return register-ready column and wall data (one record per physical column or wall leg in mm) |
| `get_beam_register_data` | structure | Return register-ready beam data with start/end supports and face-to-face clear span (mm) |
| `store_project_data` | data | Store project metadata in local database |
| `store_room_data` | data | Store room metadata in local database |
| `query_stored_data` | data | Query stored project and room data |
| `send_code_to_revit` | automation | Send C# code to Revit to execute |
| `say_hello` | core | Display a greeting dialog in Revit (connection test) |

## Development

```bash
npm install
npm run build
```

## License

[MIT](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit/blob/main/LICENSE)
