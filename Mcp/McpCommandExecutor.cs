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
                Views = args.StringList("views"),
                Grids = args.StringList("grids"),
                Links = args.StringList("links"),
                HostOnly = args.Bool("hostOnly") ?? false,
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
                    Views = args.StringList("views"),
                    Elements = args.ObjectList("elements").Select(e => new ElementPick { Id = e.Long("id") ?? 0, Link = e.String("link") }).ToList(),
                    Categories = args.StringList("categories"),
                    NameContains = args.String("nameContains"),
                    Links = args.StringList("links"),
                    HostOnly = args.Bool("hostOnly") ?? false,
                    LinkElements = args.Bool("linkElements") ?? false,
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

            // Кабельный журнал: версия без окон
            Register("export_cable_journal", (app, args) => CableJournalOperations.Export(ActiveProject(app), new CableJournalRequest
            {
                System = args.String("system"),
                Comments = args.String("comments"),
                ParameterStandard = args.String("parameterStandard"),
                TemplatePath = args.String("templatePath"),
                SavePath = args.String("savePath"),
                Overwrite = args.Bool("overwrite") ?? false,
                DryRun = args.Bool("dryRun") ?? true,
            }));

            // Маркировка под кабельный журнал
            Register("mark_devices", (app, args) => DeviceMarkingOperations.Mark(ActiveProject(app), new MarkDevicesRequest
            {
                Ids = args.StringList("ids").Select(long.Parse).ToList(),
                System = args.String("system"),
                Building = args.String("building"),
                Controller = args.String("controller"),
                Bco = args.String("bco"),
                Line = args.String("line"),
                Comments = args.String("comments"),
                Floor = args.String("floor"),
                ParameterStandard = args.String("parameterStandard"),
                StartNumber = args.Int("startNumber") ?? 1,
                Order = args.String("order") ?? "manual",
                RowMm = args.Double("rowMm") ?? 3000,
                ControllerElementId = args.Long("controllerElementId"),
                DryRun = args.Bool("dryRun") ?? true,
            }));

            // Проверка параметров плагина в проекте
            Register("check_project_parameters", (app, args) =>
                PluginParameterCheck.Run(ActiveProject(app), app.Application, args.Bool("dryRun") ?? true));

            // Размещение извещателей
            Register("list_detector_families", (app, args) => new Dictionary<string, object>
            {
                ["families"] = DetectorPlacementOperations.ListFamilies(ActiveProject(app)),
            });
            Register("place_detectors", (app, args) => DetectorPlacementOperations.Place(ActiveProject(app), new PlaceDetectorsRequest
            {
                View = args.String("view"),
                Room = args.String("room"),
                RoomId = args.Long("roomId"),
                Link = args.String("link"),
                Detector = args.String("detector"),
                FamilyType = args.String("familyType"),
                Coverage = args.Int("coverage") ?? 1,
                CeilingHeightMm = args.Double("ceilingHeightMm"),
                WallOffsetMm = args.Double("wallOffsetMm") ?? 500,
                Links = args.StringList("links"),
                DryRun = args.Bool("dryRun") ?? true,
            }));

            // Настройки маркировки текущего пользователя
            Register("get_marking_settings", (app, args) => MarkingSettings.ToResult(ActiveProject(app)));
            Register("set_marking_settings", (app, args) => MarkingSettings.Merge(ActiveProject(app),
                args.ObjectList("families").Select(f => new FamilySetting
                {
                    Name = f.String("name"),
                    Bco = f.String("bco"),
                    Aps = f.Bool("aps") ?? false,
                    Security = f.Bool("security") ?? false,
                    Souye = f.Bool("souye") ?? false,
                }).ToList(),
                JournalChanges(args)));

            Register("select_marking_profile", (app, args) =>
            {
                MarkingSettings.Select(args.String("name"));
                return MarkingSettings.ToResult(ActiveProject(app));
            });
            Register("export_marking_account", (app, args) =>
            {
                MarkingSettings.Export(args.String("path"));
                return new Dictionary<string, object> { ["saved"] = args.String("path") };
            });
            Register("import_marking_account", (app, args) =>
            {
                MarkingSettings.AddFromFile(args.String("path"));
                return MarkingSettings.ToResult(ActiveProject(app));
            });

            // Правка размеченной линии
            Register("list_marked_lines", (app, args) => DeviceLineOperations.ListLinesResult(ActiveProject(app)));
            Register("check_journal_levels", (app, args) => JournalLevelCheck.Run(ActiveProject(app), args.Double("attachMm") ?? 600));
            Register("assign_zkps", (app, args) => ZkpsOperations.Assign(ActiveProject(app), args.String("groupContains"), args.Double("cellMm") ?? 150, args.Bool("dryRun") ?? true, args.String("commentsContains")));
            Register("assign_devices_to_lines", (app, args) => LineAssignmentOperations.Assign(ActiveProject(app), args.Double("maxDistanceMm") ?? 500));
            Register("add_device_to_line", (app, args) => DeviceLineOperations.Add(ActiveProject(app),
                args.String("line"), args.Long("afterId"),
                args.Long("deviceId") ?? throw new ArgumentException("Укажите deviceId."),
                args.Bool("dryRun") ?? true));
            Register("remove_devices_from_line", (app, args) => DeviceLineOperations.Remove(ActiveProject(app), args.String("line"),
                args.StringList("ids").Select(long.Parse).ToList(), args.Bool("dryRun") ?? true));

            // Выделение и показ элементов
            Register("get_selected_elements", (app, args) => SelectionOperations.GetSelected(ActiveUIDocument(app)));
            Register("show_elements", (app, args) => SelectionOperations.Show(ActiveUIDocument(app),
                args.StringList("ids").Select(long.Parse).ToList(), args.String("action") ?? "zoom"));

            // Раскраска по параметру
            Register("color_by_parameter", (app, args) => ColorOperations.ColorByParameter(ActiveProject(app),
                args.String("view"), args.StringList("categories"), args.String("parameter"),
                args.Bool("reset") ?? false, args.Bool("dryRun") ?? true));

            // Спецификации
            Register("list_schedules", (app, args) => ScheduleOperations.List(ActiveProject(app)));
            Register("get_schedule_data", (app, args) => ScheduleOperations.Read(ActiveProject(app), args.String("schedule")));
            Register("export_schedule", (app, args) => ScheduleOperations.Export(ActiveProject(app),
                args.String("schedule"), args.String("folder"), args.String("fileName")));
            Register("create_schedule", (app, args) => ScheduleOperations.Create(ActiveProject(app),
                args.String("category"), args.String("name"), args.StringList("fields"), args.String("sortBy"), args.Bool("dryRun") ?? true));

            // Экспорт листов
            Register("export_sheets", (app, args) => ExportOperations.ExportSheets(ActiveProject(app),
                args.StringList("sheets"), args.String("format"), args.String("folder"), args.String("dwgSetup"),
                args.Bool("combine") ?? false, args.Bool("dryRun") ?? true));

            // Проверки модели и очистка
            Register("find_untagged_elements", (app, args) => ModelCheckOperations.Untagged(ActiveProject(app), args.String("view"), args.StringList("categories")));
            Register("find_undimensioned_elements", (app, args) => ModelCheckOperations.Undimensioned(ActiveProject(app), args.String("view"), args.StringList("categories")));
            Register("get_warnings", (app, args) => ModelCheckOperations.Warnings(ActiveProject(app)));
            Register("wipe_empty_tags", (app, args) => ModelCheckOperations.WipeEmptyTags(ActiveProject(app), args.String("view"), args.Bool("dryRun") ?? true));

            // Сведения о проекте и модели
            Register("get_project_info", (app, args) => ProjectInfoOperations.ProjectInfo(ActiveProject(app)));
            Register("get_current_view_info", (app, args) => ProjectInfoOperations.ViewInfo(ActiveProject(app)));
            Register("get_worksets", (app, args) => ProjectInfoOperations.Worksets(ActiveProject(app)));
            Register("get_phases", (app, args) => ProjectInfoOperations.Phases(ActiveProject(app)));
            Register("analyze_model_statistics", (app, args) => ProjectInfoOperations.Statistics(ActiveProject(app)));
            Register("check_model_health", (app, args) => ProjectInfoOperations.Health(ActiveProject(app)));
            Register("manage_links", (app, args) => ProjectInfoOperations.Links(ActiveProject(app), args.String("action"), args.StringList("links"), args.Bool("dryRun") ?? true));

            // Поиск, измерения, коллизии, данные
            Register("filter_elements", (app, args) => ElementQueryOperations.Filter(ActiveProject(app), FilterArgs(args)));
            Register("get_elements_in_spatial_volume", (app, args) =>
            {
                ElementFilterRequest request = FilterArgs(args);
                if (request.MinMm == null || request.MaxMm == null) throw new ArgumentException("Укажите minMm и maxMm.");
                return ElementQueryOperations.Filter(ActiveProject(app), request);
            });
            Register("measure_between_elements", (app, args) => ElementQueryOperations.Measure(ActiveProject(app),
                args.Long("firstId") ?? throw new ArgumentException("Укажите firstId."), args.Long("secondId") ?? throw new ArgumentException("Укажите secondId.")));
            Register("clash_detection", (app, args) => ClashOperations.Find(ActiveProject(app), new ClashRequest
            {
                CategoriesA = args.StringList("categoriesA"),
                CategoriesB = args.StringList("categoriesB"),
                View = args.String("view"),
                IncludeLinks = args.Bool("includeLinks") ?? true,
                Links = args.StringList("links"),
                MinVolumeCm3 = args.Double("minVolumeCm3") ?? 50,
                Limit = args.Int("limit") ?? 300,
            }));
            Register("export_elements_data", (app, args) => ElementQueryOperations.ExportCsv(ActiveProject(app), FilterArgs(args), args.StringList("parameters"), args.String("path")));

            // Материалы
            Register("get_materials", (app, args) => ElementQueryOperations.Materials(ActiveProject(app), args.String("nameContains"), args.String("materialClass")));
            Register("get_material_properties", (app, args) => ElementQueryOperations.MaterialProperties(ActiveProject(app), args.String("material")));
            Register("get_material_quantities", (app, args) => ElementQueryOperations.MaterialQuantities(ActiveProject(app), FilterArgs(args)));

            // Изменение элементов
            Register("delete_elements", (app, args) => ElementEditOperations.Delete(ActiveProject(app), Ids(args, "ids"), args.Bool("dryRun") ?? true));
            Register("change_element_type", (app, args) => ElementEditOperations.ChangeType(ActiveProject(app), Ids(args, "ids"),
                args.String("category"), args.String("fromType"), args.String("toType"), args.Bool("dryRun") ?? true));
            Register("modify_elements", (app, args) => ElementEditOperations.Modify(ActiveProject(app), Ids(args, "ids"), args.String("action"),
                args.Double("dxMm") ?? 0, args.Double("dyMm") ?? 0, args.Double("dzMm") ?? 0, args.Double("angle") ?? 0, args.String("axis"), args.Bool("dryRun") ?? true));
            Register("copy_elements", (app, args) => ElementEditOperations.CopyToViews(ActiveProject(app), Ids(args, "ids"),
                args.String("fromView"), args.StringList("toViews"), args.Bool("dryRun") ?? true));
            Register("match_element_properties", (app, args) => ElementEditOperations.MatchProperties(ActiveProject(app),
                args.Long("sourceId") ?? throw new ArgumentException("Укажите sourceId."), Ids(args, "targetIds"), args.StringList("parameters"), args.Bool("dryRun") ?? true));
            Register("set_element_phase", (app, args) => ElementEditOperations.SetPhase(ActiveProject(app), Ids(args, "ids"),
                args.String("created"), args.String("demolished"), args.Bool("dryRun") ?? true));
            Register("set_element_workset", (app, args) => ElementEditOperations.SetWorkset(ActiveProject(app), Ids(args, "ids"), args.String("workset"), args.Bool("dryRun") ?? true));
            Register("load_family", (app, args) => ElementEditOperations.LoadFamily(ActiveProject(app), args.String("path"), args.Bool("dryRun") ?? true));
            Register("override_graphics", (app, args) => ElementEditOperations.OverrideGraphics(ActiveProject(app), args.String("view"), Ids(args, "ids"),
                args.String("color"), args.Int("lineWeight"), args.Int("transparency"), args.Bool("halftone"), args.Bool("reset") ?? false, args.Bool("dryRun") ?? true));

            // Аннотации и изменения листов
            Register("create_text_note", (app, args) => AnnotationOperations.TextNote(ActiveProject(app), args.String("view"),
                args.Double("xMm") ?? throw new ArgumentException("Укажите xMm."), args.Double("yMm") ?? throw new ArgumentException("Укажите yMm."),
                args.String("text"), args.String("type"), args.Bool("dryRun") ?? true));
            Register("create_filled_region", (app, args) => AnnotationOperations.FilledRegion(ActiveProject(app), args.String("view"),
                args.DoubleArray("pointsMm"), args.String("type"), args.Bool("dryRun") ?? true));
            Register("manage_revisions", (app, args) => AnnotationOperations.Revisions(ActiveProject(app), args.String("action"), args.String("description"),
                args.String("date"), args.String("issuedBy"), args.String("issuedTo"), args.String("revision"), args.StringList("sheets"), args.Bool("dryRun") ?? true));
            Register("tag_equipment", (app, args) => AnnotationOperations.TagElements(ActiveProject(app), args.String("view"), args.StringList("categories"),
                args.String("tagType"), args.Double("offsetMm") ?? 300, args.Bool("leader") ?? false, args.Bool("dryRun") ?? true));

            // Переименование и очистка
            Register("batch_rename", (app, args) => CleanupOperations.BatchRename(ActiveProject(app), args.String("target"), args.StringList("categories"),
                args.String("contains"), args.String("find"), args.String("replace"), args.String("prefix"), args.String("suffix"), args.Bool("dryRun") ?? true));
            Register("cad_link_cleanup", (app, args) => CleanupOperations.CadCleanup(ActiveProject(app), args.String("action"), args.String("scope"),
                args.StringList("ids").Select(long.Parse).ToList(), args.Bool("dryRun") ?? true));
            Register("purge_unused", (app, args) => CleanupOperations.PurgeUnused(ActiveProject(app), args.StringList("kinds"), args.StringList("categories"), args.Bool("dryRun") ?? true));

            // Анализ модели: помещения, ограждающие конструкции, проверки
            Register("get_rooms", (app, args) => RoomAnalysisOperations.Rooms(ActiveProject(app), RoomArgs(args), args.Bool("includeBoundary") ?? false));
            Register("get_room_surface_areas", (app, args) => RoomAnalysisOperations.SurfaceAreas(ActiveProject(app), RoomArgs(args),
                args.Bool("subtractOpenings") ?? true, args.Bool("includeBreakdown") ?? true));
            Register("get_room_openings", (app, args) => RoomAnalysisOperations.RoomOpenings(ActiveProject(app), RoomArgs(args), args.Bool("includeDetails") ?? true));
            Register("get_spaces", (app, args) => RoomAnalysisOperations.Spaces(ActiveProject(app), args.String("level"), Ids(args, "ids")));
            Register("get_doors_windows", (app, args) => BuildingElementOperations.DoorsWindows(ActiveProject(app), args.String("category"), args.String("level"),
                args.Bool("includeLinks") ?? false, args.StringList("links"), args.Int("maxCount") ?? 1000));
            Register("get_wall_info", (app, args) => BuildingElementOperations.WallInfo(ActiveProject(app), Ids(args, "ids"), args.String("link")));
            Register("find_walls_near_point", (app, args) => BuildingElementOperations.WallsNear(ActiveProject(app),
                args.Double("xMm") ?? throw new ArgumentException("Укажите xMm."), args.Double("yMm") ?? throw new ArgumentException("Укажите yMm."),
                args.Double("radiusMm") ?? 1000, args.String("level"), args.Bool("includeLinks") ?? true));
            Register("check_detectors", (app, args) => DetectorCheckOperations.Check(ActiveProject(app), args.String("view"),
                args.StringList("smokeKeywords"), args.StringList("heatKeywords"), args.Int("coverage") ?? 1, args.Bool("checkVents") ?? true));
            Register("check_mep_spacing", (app, args) => MepSpacingOperations.Check(ActiveProject(app),
                args.Double("clearanceMm") ?? throw new ArgumentException("Укажите clearanceMm: требуемый зазор задаёт проектировщик."),
                args.StringList("categories"), args.Bool("includeLinks") ?? true, args.Bool("includeInsulation") ?? true,
                args.DoubleArray("minMm"), args.DoubleArray("maxMm"), args.Int("maxResults") ?? 300));
            Register("get_field_values", (app, args) => ModelInsightOperations.FieldValues(ActiveProject(app), args.StringList("categories"),
                args.String("parameter"), args.String("link"), args.String("view")));
            Register("get_view_extents", (app, args) => ModelInsightOperations.ViewExtents(ActiveProject(app), args.String("view")));

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

            // Значения параметров
            Register("get_parameter_values", (app, args) => ParameterValueOperations.Get(
                ActiveProject(app), ToSelection(args), args.StringList("parameters"), args.Bool("listParameterNames") ?? false));
            Register("set_parameter_values", (app, args) =>
            {
                var request = new SetValuesRequest
                {
                    Selection = ToSelection(args),
                    Values = args.StringMap("values"),
                    Items = args.ObjectList("items")
                        .Select(i => (i.Long("id") ?? 0, i.StringMap("values")))
                        .ToList(),
                    AllowTypeParameters = args.Bool("allowTypeParameters") ?? false,
                    MaxElements = args.Int("maxElements") ?? 1000,
                    DryRun = args.Bool("dryRun") ?? true,
                };
                return ParameterValueOperations.Set(ActiveProject(app), request);
            });
        }

        private static ElementSelection ToSelection(McpArgs args) => new ElementSelection
        {
            Ids = args.StringList("ids").Select(long.Parse).ToList(),
            Categories = args.StringList("categories"),
            View = args.String("view"),
            Link = args.String("link"),
            Types = args.Bool("types") ?? false,
            Where = args.ObjectList("where").Select(w => new ValueCondition
            {
                Parameter = w.String("parameter"),
                Operator = w.String("operator") ?? "equals",
                Value = w.String("value"),
            }).ToList(),
        };

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

            var timer = System.Diagnostics.Stopwatch.StartNew();
            string arguments;
            try
            {
                // Только для лога: большой или необычный набор аргументов не должен ронять команду
                arguments = PluginLog.Short(new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(request.Parameters));
            }
            catch (Exception ex)
            {
                arguments = $"(аргументы не записаны в лог: {ex.Message})";
            }
            try
            {
                ViewSession.Ui = uiApp.ActiveUIDocument;
                object data;
                try
                {
                    data = handler(uiApp, new McpArgs(request.Parameters));
                }
                finally
                {
                    ViewSession.Ui = null;
                }
                PluginLog.Info("mcp", $"{name} | OK | {timer.ElapsedMilliseconds} мс | {arguments}");
                return McpResponse.Ok(request.RequestId, data);
            }
            catch (Exception ex)
            {
                McpLog.Error($"Команда {name} завершилась с ошибкой", ex);
                PluginLog.Warn("mcp", $"{name} | ERROR {PluginLog.Short(ex.Message, 300)} | {timer.ElapsedMilliseconds} мс | {arguments}" +
                    (ex is ArgumentException ? "" : " | " + PluginLog.Short(ex.ToString(), 1500)));
                return McpResponse.Fail(request.RequestId, ex.Message);
            }
        }

        /// <summary>
        /// Запасы кабеля: меняются только переданные значения, остальные остаются из профиля.
        /// Ни одного не передано — запасы не трогаются (null).
        /// </summary>
        private static JournalSettings JournalChanges(McpArgs args)
        {
            double? aps = args.Double("apsPercent"), security = args.Double("securityPercent"),
                souye = args.Double("souyePercent"), line = args.Double("lineReserveM");
            if (aps == null && security == null && souye == null && line == null) return null;
            JournalSettings current = MarkingSettings.CurrentJournal();
            return new JournalSettings
            {
                ApsPercent = aps ?? current.ApsPercent,
                SecurityPercent = security ?? current.SecurityPercent,
                SouyePercent = souye ?? current.SouyePercent,
                LineReserveM = line ?? current.LineReserveM,
            };
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

        private static List<long> Ids(McpArgs args, string key) => args.StringList(key).Select(long.Parse).ToList();

        private static RoomQuery RoomArgs(McpArgs args) => new RoomQuery
        {
            Source = args.String("source") ?? "all",
            Links = args.StringList("links"),
            Level = args.String("level"),
            Numbers = args.StringList("numbers"),
            Ids = Ids(args, "ids"),
        };

        private static ElementFilterRequest FilterArgs(McpArgs args) => new ElementFilterRequest
        {
            Categories = args.StringList("categories"),
            FamilyContains = args.String("familyContains"),
            TypeContains = args.String("typeContains"),
            Level = args.String("level"),
            View = args.String("view"),
            MinMm = args.DoubleArray("minMm"),
            MaxMm = args.DoubleArray("maxMm"),
        };

        private static UIDocument ActiveUIDocument(UIApplication app) =>
            app.ActiveUIDocument ?? throw new InvalidOperationException("В Revit нет открытого документа.");

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
