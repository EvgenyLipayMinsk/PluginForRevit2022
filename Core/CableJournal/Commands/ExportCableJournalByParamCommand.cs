using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Revit;
using ARBM.CableJournalExporter.Services;
using ARBM.CableJournalExporter.Services;
using ARBM.CableJournalExporter.Systems.Security.Models;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;

namespace ARBM.CableJournalExporter.Commands
{
    /// <summary>
    /// Команда для экспорта кабельного журнала с фильтрацией по параметру Comments.
    /// 
    /// Это основная команда для экспорта кабельного журнала системы охранной сигнализации
    /// с возможностью фильтрации устройств по значению системного параметра "Комментарии".
    /// 
    /// Основной рабочий процесс (workflow):
    /// 1. Пользователь выбирает один элемент в документе (из категорий: MechanicalEquipment, ElectricalEquipment, DataDevices)
    /// 2. Значение параметра "Comments" выбранного элемента становится фильтром для экспорта
    /// 3. Система собирает все контроллеры и устройства, соответствующие этому фильтру
    /// 4. Строит цепочки устройств и вычисляет длины кабелей
    /// 5. Экспортирует данные в Excel на основе шаблона
    /// 
    /// Преимущества использования:
    /// • Позволяет работать с подмножеством данных (фильтрация по системам, участкам, проектам)
    /// • Упрощает работу с большими проектами, разбивая их на логические части
    /// • Гибкий параметр фильтрации (можно использовать для любых классификаций)
    /// 
    /// Блоки обработки:
    /// 1️⃣ Выбор элемента с параметром Comments
    /// 2️⃣ Выбор файла шаблона Excel
    /// 3️⃣ Валидация структуры шаблона
    /// 4️⃣ Сбор данных контроллеров с фильтрацией
    /// 5️⃣ Преобразование в CableRecord
    /// 6️⃣ Выбор пути сохранения файла
    /// 7️⃣ Экспорт в Excel
    /// 
    /// EN:
    /// Command for exporting cable journal with filtering by Comments parameter.
    /// 
    /// This is the main command for exporting the cable journal of a security alarm system
    /// with the ability to filter devices by the value of the system parameter "Comments".
    /// 
    /// Main workflow:
    /// 1. User selects one element in the document (from categories: MechanicalEquipment, ElectricalEquipment, DataDevices)
    /// 2. The value of the "Comments" parameter of the selected element becomes the export filter
    /// 3. The system collects all controllers and devices matching this filter
    /// 4. Builds device chains and calculates cable lengths
    /// 5. Exports data to Excel based on a template
    /// 
    /// Advantages:
    /// • Allows working with data subsets (filtering by systems, sections, projects)
    /// • Simplifies work with large projects by breaking them into logical parts
    /// • Flexible filter parameter (can be used for any classifications)
    /// 
    /// Processing blocks:
    /// 1️⃣ Select element with Comments parameter
    /// 2️⃣ Select Excel template file
    /// 3️⃣ Validate template structure
    /// 4️⃣ Collect controller data with filtering
    /// 5️⃣ Convert to CableRecord
    /// 6️⃣ Select file save path
    /// 7️⃣ Export to Excel
    /// 
    /// <remarks>
    /// Требования:
    /// • Все элементы должны иметь установленный параметр "Comments" с непустым значением
    /// • Шаблон Excel должен соответствовать ожидаемой структуре (проверяется TemplateValidator)
    /// • Контроллеры должны следовать соглашению о наименовании (ParseControllerId)
    /// • Устройства должны иметь установленный адрес в формате CONTROLLER.LOOP
    /// 
    /// EN: Requirements:
    /// • All elements must have the "Comments" parameter set with a non-empty value
    /// • Excel template must match the expected structure (checked by TemplateValidator)
    /// • Controllers must follow the naming convention (ParseControllerId)
    /// • Devices must have a set address in the format CONTROLLER.LOOP
    /// 
    /// Обработка ошибок:
    /// • Если пользователь отменит выбор элемента → возврат Result.Cancelled
    /// • Если Comments пуст → показать ошибку и вернуть Result.Cancelled
    /// • Если шаблон невалиден → показать ошибку структуры и вернуть Result.Failed
    /// • Если контроллеры не найдены → вернуть Result.Cancelled
    /// • Если не было сгенерировано записей → показать уведомление и вернуть Result.Cancelled
    /// • Если пользователь не выбрал путь сохранения → вернуть Result.Cancelled
    /// • Для всех остальных исключений → показать сообщение об ошибке и вернуть Result.Failed
    /// 
    /// EN: Error handling:
    /// • If user cancels element selection → return Result.Cancelled
    /// • If Comments is empty → show error and return Result.Cancelled
    /// • If template is invalid → show structure error and return Result.Failed
    /// • If controllers not found → return Result.Cancelled
    /// • If no records were generated → show notification and return Result.Cancelled
    /// • If user did not select save path → return Result.Cancelled
    /// • For all other exceptions → show error message and return Result.Failed
    /// </remarks>
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ExportCableJournalByParamCommand : IExternalCommand
    {
        /// <summary>
        /// Основной метод выполнения команды.
        /// 
        /// Интерпретируется средой Revit при вызове команды пользователем.
        /// Координирует все этапы экспорта кабельного журнала с фильтрацией.
        /// 
        /// Жизненный цикл выполнения:
        /// 1. Инициализируются необходимые сервисы и утилиты
        /// 2. Запрашивается выбор фильтрующего элемента у пользователя
        /// 3. Извлекается значение параметра Comments из выбранного элемента
        /// 4. Запрашивается выбор файла шаблона Excel
        /// 5. Валидируется структура выбранного шаблона
        /// 6. Собираются контроллеры и устройства с применением фильтра
        /// 7. Генерируются CableRecord записи с расчётом длин кабелей
        /// 8. Запрашивается путь для сохранения результата
        /// 9. Выполняется экспорт в Excel
        /// 10. Выводится сообщение об успешном завершении
        /// 
        /// EN: Main command execution method.
        /// 
        /// Interpreted by the Revit environment when the command is invoked by the user.
        /// Coordinates all stages of cable journal export with filtering.
        /// 
        /// Execution lifecycle:
        /// 1. Required services and utilities are initialized
        /// 2. User is prompted to select a filtering element
        /// 3. The Comments parameter value is extracted from the selected element
        /// 4. User is prompted to select an Excel template file
        /// 5. The structure of the selected template is validated
        /// 6. Controllers and devices are collected with filter applied
        /// 7. CableRecord entries are generated with cable length calculations
        /// 8. User is prompted for the save path
        /// 9. Export to Excel is performed
        /// 10. Success message is displayed
        /// </summary>
        /// <param name="commandData">
        /// Данные команды, содержащие ссылку на приложение Revit и активный документ.
        /// EN: Command data containing reference to Revit application and active document.
        /// </param>
        /// <param name="message">
        /// Сообщение об ошибке (выходной параметр).
        /// Может быть заполнено в случае возникновения ошибок для вывода пользователю.
        /// EN: Error message (output parameter).
        /// Can be filled in case of errors for display to the user.
        /// </param>
        /// <param name="elements">
        /// Коллекция элементов для обработки (выходной параметр).
        /// EN: Collection of elements for processing (output parameter).
        /// </param>
        /// <returns>
        /// Результат выполнения команды:
        /// • Result.Succeeded (успех) — экспорт завершен успешно
        /// • Result.Failed (ошибка) — возникла критическая ошибка при выполнении
        /// • Result.Cancelled (отмена) — пользователь отменил операцию или данные неполные
        /// 
        /// EN: Command execution result:
        /// • Result.Succeeded (success) — export completed successfully
        /// • Result.Failed (failure) — a critical error occurred during execution
        /// • Result.Cancelled (cancelled) — user cancelled the operation or data is incomplete
        /// </returns>
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            // Получаем интерфейс для работы с документом
            // EN: Get interface for working with the document
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // =================================================================
                // БЛОК 1️⃣: Выбор элемента с параметром Comments
                // BLOCK 1️⃣: Select element with Comments parameter
                // =================================================================
                
                // Информируем пользователя о необходимом действии
                // EN: Inform user about the required action
                TaskDialog.Show("Select element",
                    "Select one element with a filled system parameter \"Comments\".\n" +
                    "Allowed categories: MechanicalEquipment, ElectricalEquipment, DataDevices.");

                // Запрашиваем выбор одного элемента, используя кастомный фильтр CommentsFilter
                // Фильтр ограничивает выбор только элементами из разрешенных категорий,
                // у которых уже установлено значение параметра Comments
                // EN: Request to select one element using custom CommentsFilter
                // The filter restricts selection to only elements from allowed categories
                // that already have Comments parameter set
                Reference pickedRef = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new CommentsFilter());

                // Если пользователь отменил выбор (нажал Escape или закрыл диалог)
                // EN: If user cancelled selection (pressed Escape or closed dialog)
                if (pickedRef == null)
                    return Result.Cancelled;

                // Получаем сам элемент из документа по ссылке
                // EN: Get the actual element from the document by reference
                Element picked = doc.GetElement(pickedRef);

                // Извлекаем значение параметра "Comments" (системный параметр Revit)
                // Используем null-conditional оператор (?.) для безопасного доступа
                // EN: Extract the value of "Comments" parameter (Revit system parameter)
                // Use null-conditional operator (?.) for safe access
                string paramValue = picked
                    .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                    ?.AsString();

                // Проверяем, что значение Comments непусто
                // EN: Verify that Comments value is not empty
                if (string.IsNullOrWhiteSpace(paramValue))
                {
                    TaskDialog.Show("Error",
                        "Selected element does not contain a valid Comments parameter.");
                    return Result.Cancelled;
                }

                // =================================================================
                // БЛОК 2️⃣: Выбор файла шаблона Excel
                // BLOCK 2️⃣: Select Excel template file
                // =================================================================
                
                // Создаём сервис для работы с файловыми диалогами
                // EN: Create service for working with file dialogs
                var dialog = new FileDialogService();
                
                // Запрашиваем у пользователя выбор файла шаблона Excel
                // EN: Request user to select Excel template file
                string templatePath = dialog.SelectTemplateFile();

                // Проверяем, был ли выбран файл
                // EN: Verify that a file was selected
                if (string.IsNullOrWhiteSpace(templatePath))
                {
                    TaskDialog.Show("Cancel",
                        "Excel template not selected. Command aborted.");
                    return Result.Cancelled;
                }

                // =================================================================
                // БЛОК 3️⃣: Валидация структуры шаблона
                // BLOCK 3️⃣: Validate template structure
                // =================================================================
                
                // Создаём валидатор для проверки структуры шаблона
                // EN: Create validator to check template structure
                var validator = new TemplateValidator();
                
                // Определяем ожидаемую структуру шаблона (какие колонки, какие значения и т.д.)
                // EN: Define the expected template structure (columns, values, etc.)
                var expectedStructure = new TemplateStructure();
                
                // Валидируем выбранный шаблон против ожидаемой структуры
                // EN: Validate selected template against expected structure
                var validation = validator.Validate(templatePath, expectedStructure);

                // Если валидация не прошла — выводим ошибку и завершаем команду
                // EN: If validation failed — show error and abort command
                if (!validation.IsValid)
                {
                    TaskDialog.Show("Template error", validation.Message);
                    return Result.Failed;
                }

                // =================================================================
                // БЛОК 4️⃣: Сбор данных контроллеров с фильтрацией
                // BLOCK 4️⃣: Collect controller data with filtering
                // =================================================================
                
                // Создаём парсер для извлечения информации из параметров элементов
                // EN: Create parser to extract information from element parameters
                var parser = new SecurityElementParser();
                
                // Создаём коллектор для сбора контроллеров и устройств из документа
                // EN: Create collector to gather controllers and devices from document
                var collector = new SecurityDataCollector(parser);

                // Собираем контроллеры и их петли устройств, отфильтрованные по значению Comments
                // Метод CollectControllersWithLoopsByParam:
                // 1. Ищет все элементы в документе категории MechanicalEquipment
                // 2. Фильтрует их по значению параметра Comments == paramValue
                // 3. Группирует устройства по контроллерам и петлям
                // 4. Возвращает структурированный список ControllerWithLoops
                // EN: Collect controllers and their device loops, filtered by Comments value
                // Method CollectControllersWithLoopsByParam:
                // 1. Search all elements in document category MechanicalEquipment
                // 2. Filter them by Comments parameter == paramValue
                // 3. Group devices by controllers and loops
                // 4. Return structured list of ControllerWithLoops
                List<ControllerWithLoops> controllers =
                    collector.CollectControllersWithLoopsByParam(doc, paramValue);

                // Проверяем, были ли найдены контроллеры
                // Если контроллеры не найдены, коллектор обычно уже выводит сообщение об ошибке
                // EN: Verify that controllers were found
                // If controllers not found, collector usually already shows error message
                if (controllers == null || controllers.Count == 0)
                    return Result.Cancelled;

                // =================================================================
                // БЛОК 5️⃣: Преобразование в CableRecord
                // BLOCK 5️⃣: Convert to CableRecord
                // =================================================================
                
                // Определяем режим вычисления длины кабеля
                // LengthMode.Route — длина по маршруту (через граф трасс)
                // EN: Define cable length calculation mode
                // LengthMode.Route — length by route (through routing graph)
                LengthMode mode = LengthMode.Route;

                // Создаём построитель CableRecord с указанными параметрами:
                // • parser — парсер для извлечения параметров из элементов
                // • doc — документ Revit для доступа к геометрии и уровням
                // • mode — режим расчета длины кабеля
                // • 1.3 — коэффициент запаса для длины кабеля (на 30% больше вычисленной)
                // EN: Create CableRecord builder with specified parameters:
                // • parser — parser to extract parameters from elements
                // • doc — Revit document for access to geometry and levels
                // • mode — cable length calculation mode
                // • 1.3 — safety factor for cable length (30% more than calculated)
                var builder = new SecurityCableRecordBuilder(parser, doc, mode, 1.3);
                
                // Строим список CableRecord из собранных контроллеров
                // Для каждого контроллера, петли и устройства создаётся CableRecord с:
                // • информацией о контроллере и устройстве
                // • расчётной длиной кабеля (с учётом коэффициента)
                // • другой необходимой информацией для экспорта в Excel
                // EN: Build list of CableRecord from collected controllers
                // For each controller, loop and device creates CableRecord with:
                // • controller and device information
                // • calculated cable length (considering safety factor)
                // • other necessary information for Excel export
                List<CableRecord> records = builder.Build(controllers, paramValue);

                // Проверяем, были ли сгенерированы какие-либо записи
                // EN: Verify that some records were generated
                if (records.Count == 0)
                {
                    TaskDialog.Show("No data",
                        "No CableRecord entries were generated.");
                    return Result.Cancelled;
                }

                // =================================================================
                // БЛОК 6️⃣: Выбор пути сохранения файла
                // BLOCK 6️⃣: Select file save path
                // =================================================================
                
                // Запрашиваем у пользователя путь для сохранения результирующего файла Excel
                // EN: Request user to select save path for resulting Excel file
                string savePath = dialog.SelectSaveLocation();

                // Проверяем, был ли выбран путь сохранения
                // EN: Verify that a save path was selected
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    TaskDialog.Show("Cancel",
                        "Save file not selected. Command aborted.");
                    return Result.Cancelled;
                }

                // =================================================================
                // БЛОК 7️⃣: Экспорт в Excel
                // BLOCK 7️⃣: Export to Excel
                // =================================================================
                
                // Создаём экспортер для преобразования данных в Excel файл
                // EN: Create exporter to convert data to Excel file
                var exporter = new ExcelExporter();
                
                // Выполняем экспорт:
                // 1. Загружаем шаблон из templatePath
                // 2. Заполняем данные из records в соответствующие ячейки
                // 3. Применяем форматирование (если определено в шаблоне)
                // 4. Сохраняем результат в файл savePath
                // EN: Perform export:
                // 1. Load template from templatePath
                // 2. Fill data from records into corresponding cells
                // 3. Apply formatting (if defined in template)
                // 4. Save result to savePath
                exporter.ExportCableRecords(templatePath, savePath, records);

                // =================================================================
                // ЗАВЕРШЕНИЕ: Уведомление об успехе
                // COMPLETION: Success notification
                // =================================================================
                
                // Выводим пользователю сообщение об успешном завершении с путём к файлу
                // EN: Show user success message with file path
                TaskDialog.Show("Done",
                    $"Cable journal successfully created:\n{savePath}");

                // Возвращаем код успешного выполнения команды
                // EN: Return success code
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                // =================================================================
                // ОБРАБОТКА ОШИБОК: Общее исключение
                // ERROR HANDLING: General exception
                // =================================================================
                
                // Если возникло необработанное исключение (не один из ожидаемых результатов),
                // выводим полный текст ошибки пользователю
                // EN: If an unhandled exception occurred (not one of expected results),
                // show full error text to user
                TaskDialog.Show("Error",
                    $"An error occurred:\n{ex.Message}");
                
                // Возвращаем код ошибки
                // EN: Return error code
                return Result.Failed;
            }
        }
    }
}
