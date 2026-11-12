# Archive: Convert File to RVT2022

Команда пакетного пересохранения семейств (.rfa) в Revit 2022.
Убрана из плагина 07.11.2026; код сохранён здесь на случай, если понадобится снова.
Файлы из этой папки **не входят в сборку** PluginForRevit2022.

## Что здесь

| Файл / папка | Назначение |
|---|---|
| `CmdConvertationForRevitVersion.cs` | Команда: читает `folder_settings_remote_path.json`, копирует папку `folder_from` → `folder_to`, открывает каждое .rfa и пересохраняет в текущей версии Revit |
| `ArbmFamiliesManager.cs` | Открытие/проверка/сохранение семейств, замена параметров AER_ → ARBM_ (часть логики была закомментирована в команде) |
| `ArbmAPIElementHelper.cs` | Вспомогательные методы Revit API (из всего файла команда использовала 3 метода) |
| `ScanFolderCopyAndProcess/` | Проект сканирования папок, логгер, чтение JSON-настроек (зависит от AlphaFS и Newtonsoft.Json) |
| `Family/` | Тестовые семейства Омега-Саунд (версии 2020 и 2022) |
| `Resourses/Convert to RVT2022.png` | Иконка кнопки |

## Известные проблемы (исправить перед возвратом)

1. `ScanFolder.Prepare` **рекурсивно удаляет `folder_to` без подтверждения**; если `folder_from` внутри `folder_to`, удалит исходники.
2. Семейства, пропущенные из-за категории/только чтения/ошибки открытия, не попадают в `folder_to`.
3. Жёсткий путь `D:\3DSD_DB_TEMPL\` в `ArbmFamiliesManager.SaveAndCloseAfterChanges`; при исключении временная папка и открытый документ остаются.
4. Окно MessageBox на каждый пропущенный файл; настройки и лог пишутся рядом с DLL.
5. В закомментированной логике параметров: падение на пустых значениях (`AsDouble/AsInteger` → `""` → `Parse`), `SetRevitVersion` пишет только в текущий тип, NRE в `ChangeNestedFamily`.

## Как вернуть

1. Перенести `.cs` файлы обратно в корень проекта и добавить их в `<Compile>` в `PluginForRevit2022.csproj`.
2. Вернуть `ScanFolderCopyAndProcess` в решение и `ProjectReference` в csproj; подключить AlphaFS и Newtonsoft.Json через NuGet.
3. Вернуть иконку в `Properties/Resources.resx` и кнопку в `App.cs`.
4. Полная версия до удаления — коммит `f54c403` в ветке `fix/critical-issues`.
