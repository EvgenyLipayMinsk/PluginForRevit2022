# AerBIM Revit MCP server

Связывает ИИ-клиент (Claude Code, Claude Desktop) с надстройкой **AerBIM Manager** в Revit.

```
Claude ──stdio──▶ McpServer/index.js ──WebSocket localhost:8965──▶ надстройка в Revit ──▶ Revit API
```

## Установка (один раз)

```bash
cd McpServer
npm install
```

Нужен Node.js 18+.

## Подключение к Claude Code

В корне репозитория лежит `.mcp.json` — Claude Code, открытый в папке проекта, предложит включить сервер `aerbim-revit`.
Вручную:

```bash
claude mcp add aerbim-revit --env AERBIM_MCP_PORT=8965 -- node "D:/Visual Studio/PluginForRevit2022/McpServer/index.js"
```

## Работа

1. Откройте проект в Revit 2022/2023/2024.
2. На вкладке **AerBIM Manager** нажмите **MCP** — кнопка станет зелёной («вкл»).
3. В Claude: «покажи параметры проекта», «удали параметр ARBM_…».

Если соединение зависло — кнопка **Сброс MCP** разрывает подключение и заново открывает порт.
Лог надстройки: `%AppData%\AerBIM\PluginForRevit\mcp.log`.

## Инструменты

| Инструмент | Что делает |
|---|---|
| `ping` | Проверка связи: версия Revit, активный документ |
| `list_project_parameters` | Параметры проекта: id, имя, общий/проекта, экземпляр/тип, категории |
| `remove_project_parameter` | Удаление по `id` или `name`. По умолчанию `dryRun=true` — только предпросмотр; удаление — с `dryRun=false`. Отменяется в Revit через Ctrl+Z |

## Новая команда

1. `Mcp/McpCommandExecutor.cs` — `case "имя_команды"` и метод (выполняется в UI-потоке Revit).
2. `McpServer/index.js` — `server.registerTool("имя_команды", …)` с описанием и схемой параметров.

## Порт

8965 (надстройка RevitMCP занимает 8964). Меняется в `Mcp/McpHost.cs` (`Port`) и в `AERBIM_MCP_PORT`.
