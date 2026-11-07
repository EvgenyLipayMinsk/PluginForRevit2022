using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PluginForRevit2022.Core;

namespace PluginForRevit2022.Mcp
{
    /// <summary>
    /// Реестр MCP-команд. Логика — в Core/*Operations (те же операции вызывают кнопки на ленте).
    /// Новая команда: Register(...) здесь + запись в McpServer/tools.json.
    /// Выполняется только в UI-потоке Revit.
    /// </summary>
    internal static class McpCommandExecutor
    {
        private static readonly Dictionary<string, Func<UIApplication, McpArgs, object>> Commands =
            new Dictionary<string, Func<UIApplication, McpArgs, object>>(StringComparer.OrdinalIgnoreCase);

        static McpCommandExecutor()
        {
            Register("ping", Ping);

            // Параметры проекта
            Register("list_project_parameters", ListProjectParameters);
            Register("remove_project_parameter", RemoveProjectParameter);

            // ФОП: общие параметры
            Register("read_shared_parameter_file", ReadSharedParameterFile);
            Register("list_bindable_categories", ListBindableCategories);
            Register("list_parameter_groups", (app, args) => new Dictionary<string, object>
            {
                ["groups"] = SharedParameterOperations.ParameterGroups().Select(g => g.ToDictionary()).ToList(),
            });
            Register("add_shared_parameters", AddSharedParameters);

            // Листы и виды
            Register("list_sheets", ListSheets);
            Register("list_title_blocks", (app, args) =>
            {
                List<TitleBlockInfo> items = SheetOperations.ListTitleBlocks(ActiveProject(app), args.Bool("measure") ?? true);
                return new Dictionary<string, object> { ["count"] = items.Count, ["titleBlocks"] = items.Select(t => t.ToDictionary()).ToList() };
            });
            Register("create_sheets", CreateSheets);
            Register("list_views", ListViews);
            Register("crop_view", CropView);
            Register("place_view_on_sheet", PlaceViewOnSheet);
            Register("place_on_sheet", PlaceOnSheet);
            Register("rename", Rename);

            // Связи
            Register("list_links", (app, args) =>
            {
                Document doc = ActiveProject(app);
                var items = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                    .Select(l => new Dictionary<string, object>
                    {
                        ["id"] = RevitIds.IdOf(l.Id),
                        ["name"] = l.Name,
                        ["loaded"] = l.GetLinkDocument() != null,
                        ["document"] = l.GetLinkDocument()?.Title,
                    }).ToList();
                return new Dictionary<string, object> { ["count"] = items.Count, ["links"] = items };
            });

            // Размеры
            Register("list_dimension_types", (app, args) => new Dictionary<string, object>
            {
                ["types"] = DimensionOperations.ListDimensionTypes(ActiveProject(app)),
            });
            Register("dimension_grids", (app, args) => DimensionOperations.DimensionGrids(ActiveProject(app), new DimensionGridsRequest
            {
                View = args.String("view"),
                Grids = args.StringList("grids"),
                IncludeLinks = args.Bool("includeLinks") ?? false,
                Side = args.String("side") ?? "both",
                OffsetMm = args.Double("offsetMm") ?? 10,
                Overall = args.Bool("overall") ?? true,
                DimensionType = args.String("dimensionType"),
                DryRun = args.Bool("dryRun") ?? true,
            }));
            Register("dimension_elements", (app, args) =>
            {
                var request = new DimensionElementsRequest
                {
                    View = args.String("view"),
                    Elements = args.ObjectList("elements").Select(e => new ElementPick { Id = e.Long("id") ?? 0, Link = e.String("link") }).ToList(),
                    Categories = args.StringList("categories"),
                    NameContains = args.String("nameContains"),
                    IncludeLinks = args.Bool("includeLinks") ?? true,
                    Links = args.StringList("links"),
                    Axis = args.String("axis") ?? "x",
                    Mode = args.String("mode") ?? "chain",
                    Walls = args.String("walls") ?? (string.Equals(args.String("mode"), "to_walls", StringComparison.OrdinalIgnoreCase) ? "nearest" : "none"),
                    MaxWallDistanceMm = args.Double("maxWallDistanceMm") ?? 20000,
                    OffsetMm = args.Double("offsetMm") ?? 8,
                    DimensionType = args.String("dimensionType"),
                    DryRun = args.Bool("dryRun") ?? true,
                };
                List<string> wallCategories = args.StringList("wallCategories");
                if (wallCategories.Count > 0) request.WallCategories = wallCategories;
                return DimensionOperations.DimensionElements(ActiveProject(app), request);
            });

            // Марки помещений
            Register("list_room_tag_types", (app, args) => new Dictionary<string, object>
            {
                ["types"] = RoomTagOperations.ListRoomTagTypes(ActiveProject(app)),
            });
            Register("tag_rooms", (app, args) => RoomTagOperations.TagRooms(ActiveProject(app), new TagRoomsRequest
            {
                View = args.String("view"),
                Links = args.StringList("links"),
                IncludeHost = args.Bool("includeHost") ?? true,
                TagType = args.String("tagType"),
                SkipTagged = args.Bool("skipTagged") ?? true,
                Placement = args.String("placement") ?? "location",
                LevelToleranceMm = args.Double("levelToleranceMm") ?? 100,
                RoomLevel = args.String("roomLevel"),
                DryRun = args.Bool("dryRun") ?? true,
            }));

            // Фильтры, графика, шаблоны
            Register("list_view_filters", (app, args) => new Dictionary<string, object>
            {
                ["filters"] = ViewGraphicsOperations.ListFilters(ActiveProject(app)),
            });
            Register("list_view_templates", (app, args) => new Dictionary<string, object>
            {
                ["templates"] = ViewGraphicsOperations.ListTemplates(ActiveProject(app)),
            });
            Register("list_filterable_parameters", (app, args) =>
                ViewGraphicsOperations.FilterableParameters(ActiveProject(app), args.StringList("categories"), args.String("nameContains")));
            Register("create_view_filter", (app, args) => ViewGraphicsOperations.CreateFilter(ActiveProject(app), new CreateFilterRequest
            {
                Name = args.String("name"),
                Categories = args.StringList("categories"),
                Rules = args.ObjectList("rules").Select(r => new FilterRuleSpec
                {
                    Parameter = r.String("parameter"),
                    Operator = r.String("operator") ?? "equals",
                    Value = r.String("value"),
                }).ToList(),
                Logic = args.String("logic") ?? "and",
                OverwriteExisting = args.Bool("overwriteExisting") ?? false,
                DryRun = args.Bool("dryRun") ?? true,
            }));
            Register("set_graphics_overrides", (app, args) =>
            {
                McpArgs o = args.Object("overrides");
                return ViewGraphicsOperations.SetOverrides(ActiveProject(app), new SetOverridesRequest
                {
                    View = args.String("view"),
                    Filter = args.String("filter"),
                    Category = args.String("category"),
                    Visible = args.Bool("visible"),
                    Reset = args.Bool("reset") ?? false,
                    DryRun = args.Bool("dryRun") ?? true,
                    Overrides = o == null ? null : new GraphicsOverrides
                    {
                        LineColor = o.String("lineColor"),
                        LineWeight = o.Int("lineWeight"),
                        LinePattern = o.String("linePattern"),
                        SurfaceColor = o.String("surfaceColor"),
                        SurfacePattern = o.String("surfacePattern"),
                        CutLineColor = o.String("cutLineColor"),
                        CutLineWeight = o.Int("cutLineWeight"),
                        CutColor = o.String("cutColor"),
                        CutPattern = o.String("cutPattern"),
                        Transparency = o.Int("transparency"),
                        Halftone = o.Bool("halftone"),
                    },
                });
            });
            Register("remove_view_filter", (app, args) =>
                ViewGraphicsOperations.RemoveFilter(ActiveProject(app), args.String("view"), args.String("filter"), args.Bool("dryRun") ?? true));
            Register("apply_view_template", (app, args) => ViewGraphicsOperations.ApplyTemplate(ActiveProject(app), new ApplyTemplateRequest
            {
                Template = args.String("template"),
                Views = args.StringList("views"),
                ViewType = args.String("viewType"),
                NameContains = args.String("nameContains"),
                DryRun = args.Bool("dryRun") ?? true,
            }));
            Register("create_view_template", (app, args) =>
                ViewGraphicsOperations.CreateTemplateFromView(ActiveProject(app), args.String("view"), args.String("name"), args.Bool("dryRun") ?? true));
        }

        public static IEnumerable<string> CommandNames => Commands.Keys;

        private static void Register(string name, Func<UIApplication, McpArgs, object> handler) => Commands[name] = handler;

        public static McpResponse Execute(UIApplication uiApp, McpRequest request)
        {
            string name = request.CommandName?.Trim() ?? string.Empty;
            if (!Commands.TryGetValue(name, out Func<UIApplication, McpArgs, object> handler))
            {
                return McpResponse.Fail(request.RequestId,
                    $"Неизвестная команда: {name}. Доступны: {string.Join(", ", Commands.Keys)}");
            }

            try
            {
                return McpResponse.Ok(request.RequestId, handler(uiApp, new McpArgs(request.Parameters)));
            }
            catch (Exception ex)
            {
                McpLog.Error($"Команда {name} завершилась с ошибкой", ex);
                return McpResponse.Fail(request.RequestId, ex.Message);
            }
        }

        // ---------- Общие ----------

        private static object Ping(UIApplication app, McpArgs args)
        {
            Document doc = app.ActiveUIDocument?.Document;
            return new Dictionary<string, object>
            {
                ["addin"] = "AerBIM Manager (PluginForRevit2022)",
                ["revitVersion"] = app.Application.VersionNumber,
                ["activeDocument"] = doc?.Title,
                ["isFamilyDocument"] = doc?.IsFamilyDocument,
                ["sharedParameterFile"] = app.Application.SharedParametersFilename,
                ["commands"] = Commands.Keys.OrderBy(k => k).ToList(),
            };
        }

        private static Document ActiveDocument(UIApplication app) =>
            app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("В Revit нет открытого документа.");

        private static Document ActiveProject(UIApplication app)
        {
            Document doc = ActiveDocument(app);
            if (doc.IsFamilyDocument)
            {
                throw new InvalidOperationException("Активен документ семейства. Эта команда работает с проектом.");
            }
            return doc;
        }

        // ---------- Параметры проекта ----------

        private static object ListProjectParameters(UIApplication app, McpArgs args)
        {
            Document doc = ActiveProject(app);
            string filter = args.String("nameContains");
            List<Dictionary<string, object>> items = ProjectParameterOperations.List(doc)
                .Where(p => filter == null || p.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .Select(p => p.ToDictionary())
                .ToList();
            return new Dictionary<string, object> { ["document"] = doc.Title, ["count"] = items.Count, ["parameters"] = items };
        }

        private static object RemoveProjectParameter(UIApplication app, McpArgs args)
        {
            Document doc = ActiveProject(app);
            ProjectParameterInfo target = ProjectParameterOperations.Resolve(doc, args.Long("id"), args.String("name"));
            Dictionary<string, object> result = ProjectParameterOperations.Remove(doc, target, args.Bool("dryRun") ?? true);
            if (!(bool)result["dryRun"])
            {
                McpLog.Info($"Удалён параметр проекта «{target.Name}» (id {target.Id})");
            }
            return result;
        }

        // ---------- ФОП ----------

        private static object ReadSharedParameterFile(UIApplication app, McpArgs args)
        {
            var (path, parameters) = SharedParameterOperations.ReadFile(app.Application, args.String("filePath"));
            string filter = args.String("nameContains");
            string group = args.String("group");
            List<Dictionary<string, object>> items = parameters
                .Where(p => filter == null || p.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .Where(p => group == null || string.Equals(p.Group, group, StringComparison.CurrentCultureIgnoreCase))
                .Select(p => p.ToDictionary())
                .ToList();
            return new Dictionary<string, object>
            {
                ["file"] = path,
                ["groups"] = parameters.Select(p => p.Group).Distinct().ToList(),
                ["count"] = items.Count,
                ["parameters"] = items,
            };
        }

        private static object ListBindableCategories(UIApplication app, McpArgs args)
        {
            Document doc = ActiveProject(app);
            string filter = args.String("nameContains");
            List<Dictionary<string, object>> items = SharedParameterOperations.BindableCategories(doc)
                .Where(c => filter == null || c.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .Select(c => new Dictionary<string, object>
                {
                    ["name"] = c.Name,
                    ["builtInCategory"] = SharedParameterOperations.BuiltInCategoryName(c),
                    ["type"] = c.CategoryType.ToString(),
                })
                .ToList();
            return new Dictionary<string, object> { ["count"] = items.Count, ["categories"] = items };
        }

        // ---------- Листы и виды ----------

        private static object ListSheets(UIApplication app, McpArgs args)
        {
            Document doc = ActiveProject(app);
            string filter = args.String("contains");
            List<string> parameterNames = args.StringList("parameters");
            List<Dictionary<string, object>> items = SheetOperations.Sheets(doc)
                .Where(s => filter == null ||
                            s.SheetNumber.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                            s.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .Select(s => SheetOperations.Describe(doc, s, parameterNames.Count > 0 ? parameterNames : null))
                .ToList();
            return new Dictionary<string, object> { ["count"] = items.Count, ["sheets"] = items };
        }

        private static SheetSpec ToSheetSpec(McpArgs item) => item == null ? null : new SheetSpec
        {
            Number = item.String("number"),
            Name = item.String("name"),
            Parameters = item.StringMap("parameters"),
        };

        private static object CreateSheets(UIApplication app, McpArgs args)
        {
            var request = new CreateSheetsRequest
            {
                TitleBlock = args.String("titleBlock"),
                Sheets = args.ObjectList("sheets").Select(ToSheetSpec).ToList(),
                DryRun = args.Bool("dryRun") ?? true,
            };
            return SheetOperations.CreateSheets(ActiveProject(app), request);
        }

        private static object ListViews(UIApplication app, McpArgs args)
        {
            Document doc = ActiveProject(app);
            string filter = args.String("nameContains");
            bool onlyNotPlaced = args.Bool("notOnSheet") ?? false;
            List<Dictionary<string, object>> items = ViewOperations.ListViews(doc, args.String("viewType") ?? "FloorPlan")
                .Where(v => filter == null || v.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .Select(v => ViewOperations.Describe(doc, v))
                .Where(d => !onlyNotPlaced || d["sheet"] == null)
                .ToList();
            return new Dictionary<string, object> { ["count"] = items.Count, ["views"] = items };
        }

        private static object CropView(UIApplication app, McpArgs args)
        {
            var request = new CropViewRequest
            {
                View = args.String("view"),
                Mode = args.String("mode") ?? "model",
                OffsetMm = args.Double("offsetMm") ?? 1000,
                Grids = args.StringList("grids"),
                RectangleMm = args.DoubleArray("rectangleMm"),
                ScopeBox = args.String("scopeBox"),
                IncludeLinks = args.Bool("includeLinks") ?? true,
                AnnotationOffsetMm = args.Double("annotationCropOffsetMm"),
                DryRun = args.Bool("dryRun") ?? true,
            };
            return ViewOperations.Crop(ActiveProject(app), request);
        }

        private static object PlaceViewOnSheet(UIApplication app, McpArgs args)
        {
            McpArgs margins = args.Object("marginsMm");
            var request = new PlaceViewRequest
            {
                View = args.String("view"),
                SheetId = args.Long("sheetId"),
                SheetNumber = args.String("sheetNumber"),
                NewSheet = ToSheetSpec(args.Object("newSheet")),
                TitleBlock = args.String("titleBlock"),
                TitleBlockFamily = args.String("titleBlockFamily"),
                FitFormat = args.Bool("fitFormat") ?? true,
                Scale = args.Int("scale"),
                StampHeightMm = args.Double("stampHeightMm") ?? 55,
                DuplicateIfPlaced = args.Bool("duplicateIfPlaced") ?? false,
                DryRun = args.Bool("dryRun") ?? true,
            };
            if (margins != null)
            {
                request.MarginLeftMm = margins.Double("left") ?? request.MarginLeftMm;
                request.MarginTopMm = margins.Double("top") ?? request.MarginTopMm;
                request.MarginRightMm = margins.Double("right") ?? request.MarginRightMm;
                request.MarginBottomMm = margins.Double("bottom") ?? request.MarginBottomMm;
            }
            return PlacementOperations.PlaceView(ActiveProject(app), request);
        }

        private static object PlaceOnSheet(UIApplication app, McpArgs args)
        {
            McpArgs margins = args.Object("marginsMm");
            var request = new PlaceOnSheetRequest
            {
                Item = args.String("item"),
                SheetId = args.Long("sheetId"),
                SheetNumber = args.String("sheetNumber"),
                Position = args.String("position") ?? "auto",
                XMm = args.Double("xMm"),
                YMm = args.Double("yMm"),
                GapMm = args.Double("gapMm") ?? 5,
                StampWidthMm = args.Double("stampWidthMm") ?? 185,
                StampHeightMm = args.Double("stampHeightMm") ?? 55,
                DuplicateIfPlaced = args.Bool("duplicateIfPlaced") ?? false,
                DryRun = args.Bool("dryRun") ?? true,
            };
            if (margins != null)
            {
                request.MarginLeftMm = margins.Double("left") ?? request.MarginLeftMm;
                request.MarginTopMm = margins.Double("top") ?? request.MarginTopMm;
                request.MarginRightMm = margins.Double("right") ?? request.MarginRightMm;
                request.MarginBottomMm = margins.Double("bottom") ?? request.MarginBottomMm;
            }
            return SheetLayoutOperations.PlaceOnSheet(ActiveProject(app), request);
        }

        private static object Rename(UIApplication app, McpArgs args)
        {
            var request = new RenameRequest
            {
                Target = args.String("target") ?? "views",
                Items = args.ObjectList("items").Select(i => new RenameItem
                {
                    Id = i.Long("id"),
                    Current = i.String("current"),
                    NewName = i.String("newName"),
                    NewNumber = i.String("newNumber"),
                }).ToList(),
                Ids = args.StringList("ids").Select(long.Parse).ToList(),
                ViewType = args.String("viewType"),
                NameContains = args.String("nameContains"),
                NumberContains = args.String("numberContains"),
                Field = args.String("field") ?? "name",
                Find = args.String("find"),
                Replace = args.String("replace"),
                Regex = args.Bool("regex") ?? false,
                Prefix = args.String("prefix"),
                Suffix = args.String("suffix"),
                Pattern = args.String("pattern"),
                Start = args.Int("start") ?? 1,
                Step = args.Int("step") ?? 1,
                Order = args.String("order") ?? "number",
                DryRun = args.Bool("dryRun") ?? true,
            };
            return RenameOperations.Rename(ActiveProject(app), request);
        }

        private static object AddSharedParameters(UIApplication app, McpArgs args)
        {
            Document doc = ActiveDocument(app);
            string binding = args.String("binding") ?? "instance";
            if (binding != "instance" && binding != "type")
            {
                throw new ArgumentException("binding: instance или type.");
            }

            var request = new AddSharedParametersRequest
            {
                FilePath = args.String("filePath"),
                Parameters = args.StringList("parameters"),
                Categories = args.StringList("categories"),
                IsInstance = binding == "instance",
                ParameterGroup = args.String("parameterGroup"),
                AllowKindChange = args.Bool("allowKindChange") ?? false,
                DryRun = args.Bool("dryRun") ?? true,
            };
            AddSharedParametersResult result = SharedParameterOperations.Add(doc, app.Application, request);
            if (!result.DryRun && result.ChangeCount > 0)
            {
                McpLog.Info($"Добавлены параметры из ФОП в «{doc.Title}»: {result.ChangeCount}");
            }
            return result.ToDictionary();
        }
    }
}
