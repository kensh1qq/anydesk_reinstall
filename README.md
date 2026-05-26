# AnyDesk Reinstaller

**by kensh1qq**

Автоматически переустанавливает AnyDesk раз в неделю, работает в System Tray, прописывается в автозапуск Windows.

## Возможности

- Работает полностью в фоне (иконка в трее)
- Автозапуск при старте Windows (реестр HKLM)
- Переустанавливает AnyDesk раз в неделю без каких-либо диалогов
- Блокирует автообновление AnyDesk
- Авто-обновление самого лаунчера через GitHub
- Встроенный UI с термометром и журналом операций

## Установка

1. Скачать `AnydeskReinstaller.exe` из [Releases](../../releases/latest)
2. Запустить — UAC запросит права администратора
3. Программа уйдёт в трей и начнёт работу

## Как выпустить обновление

1. Собрать новый EXE (см. `src/`)
2. Создать новый Release на GitHub, загрузить EXE
3. Обновить `version.json`:
```json
{"version":"1.0.1","url":"https://github.com/kensh1qq/anydesk_reinstall/releases/download/v1.0.1/AnydeskReinstaller.exe"}
```

Все запущенные копии автоматически скачают и применят обновление.

## Сборка из исходников

Требуется .NET 8 SDK:
```
dotnet publish src/AnydeskReinstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```