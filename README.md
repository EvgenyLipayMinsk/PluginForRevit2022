# Plugin For Revit 2022

## Запуск плагина в Visual Studio

Что бы начать работу с плагином Вам потребуется установить Autodesk Revit 2022, далее в проекте нужно обновить ссылки для следующих .dll: RevitUI.dll и RevitUIAPI.dll

![image](https://github.com/EvgenyLipayMinsk/PluginForRevit2022/assets/69685960/adf4d4cd-5d7b-4de7-b1bf-533251bbfc7f)

Далее при запуске отладки установить "Запуск от внешней программы: Revit.exe"

![image](https://github.com/EvgenyLipayMinsk/PluginForRevit2022/assets/69685960/4e83dbe1-5aed-4c46-8113-1be73e947718)

Скопировать файл "PluginForRevit2022.addin" по пути: C:\ProgramData\Autodesk\Revit\Addins\2022
В файле "PluginForRevit2022.addin" изменить путь до PluginForRevit2022.dll в строке Assembly:

![image](https://github.com/EvgenyLipayMinsk/PluginForRevit2022/assets/69685960/061cbc21-5e96-45a6-b888-c5f0df629921)

## Запуск плагина в Visual Studio

### Remove Parameters

Клавиша "Remove Parameters" ![image](https://github.com/EvgenyLipayMinsk/PluginForRevit2022/assets/69685960/f8980660-7935-4c3c-96fe-92b82260b9a8) позволяет удалять выделеные параметры из проекта:

![image](https://github.com/EvgenyLipayMinsk/PluginForRevit2022/assets/69685960/44749228-542b-4ba3-8992-2b4089dc0768)

При удалении параметров появится окно с списком удаленных параметров:

![image](https://github.com/EvgenyLipayMinsk/PluginForRevit2022/assets/69685960/3cca7a79-2e07-4e0a-9560-937ee51d4ec3)

### Convert File to RVT2022

Клавиша "Convert File to RVT2022" ![image](https://github.com/EvgenyLipayMinsk/PluginForRevit2022/assets/69685960/5729c78b-ac45-46a6-995d-684aa9ee7d9b) пересохраняет семейства в Revit 2022 и каталог файлов из одного каталога в другой.

Для этого нужно в файле "folder_settings_remote_path" указать пути(from,to):

![image](https://github.com/EvgenyLipayMinsk/PluginForRevit2022/assets/69685960/0a91523e-76d4-4565-9739-6745dbdc3a0d)


