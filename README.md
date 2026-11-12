# WaterMarker

Настольное приложение Windows для подготовки персональных копий изображений,
скриншотов и видео с видимой меткой конфиденциальности. Полный код на копии связывает
её с записью об адресате и указанном оператором канале. Запись фиксирует подготовку
копии и сама по себе не подтверждает отправку или источник утечки.

## Установка и запуск из исходников

Требуются Windows 11 x64, Git и [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
версии, указанной в `global.json` (10.0.403 либо более новый патч 10.0.4xx).
Поддержка [WPF входит в .NET 10](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100).

Откройте PowerShell в каталоге проекта:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Environment.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Verify.ps1
. ./scripts/Initialize-DotNet.ps1
dotnet run --project src/WaterMarker.App
```

Команда запуска открывает главное окно WaterMarker. Инструкции по сборке и устройству
модулей находятся в [руководстве разработчика](docs/DEVELOPMENT.md).

## FFmpeg

Для работы с видео нужны отдельные `ffmpeg.exe` и `ffprobe.exe` в `PATH`,
а в FFmpeg — кодировщик `libx264`. Бинарники не хранятся в репозитории.
Проверенная при подготовке окружения сборка: `8.0.1-full_build-www.gyan.dev`,
GCC 15.2.0. Проверены запуск обеих утилит и наличие `libx264`;
это не подтверждает качество экспорта видео приложением.

1. Перейдите к [сборкам Windows на странице FFmpeg](https://ffmpeg.org/download.html#build-windows).
2. Скачайте full build x64 от gyan.dev и распакуйте в отдельный каталог.
3. Добавьте каталог `bin`, содержащий обе утилиты, в пользовательский `PATH` и откройте новый PowerShell.
4. Выполните проверку:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-Environment.ps1 -RequireVideo
```

Зависимости FFmpeg требуются только для видео. Для будущего включения бинарников
в дистрибутив необходима проверка [условий конкретной сборки](https://ffmpeg.org/legal.html).
