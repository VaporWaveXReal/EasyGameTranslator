# EasyGameTranslator 0.1 Beta

Экранный переводчик английского текста для игр и приложений Windows. Программа
захватывает выбранное окно, распознаёт текст встроенным Windows OCR, переводит
его через Яндекс и показывает перевод поверх оригинала.

> Текущая версия: **EasyGameTranslator 0.1 Beta**. Возможны ошибки распознавания и совместимости с
> отдельными играми.

## ❤️ Поддержать проект

Если EasyGameTranslator оказался полезен, вы можете поддержать дальнейшую
разработку:

| Валюта | Сеть | Адрес |
|---|---|---|
| Gram (TonCoin) | TON | `UQAq90X97DbxDJ_6B9IU14qBE5W4cGvjWNUTFIjybPRsPEp1` |
| Bitcoin | Bitcoin | `bc1qwrzljw6gx3rdrf6428q5p6mgt3pf24fsnuqwjh` |
| ETH | Ethereum | `0x5D8b7f8Ec58E03C56B806B11A52eC2c6D16f5a1d` |
| USDT | TRON (TRC-20) | `TJ3hyPm5fFWrf3XqbaL4C5TTWVjf3TP7sw` |
| USDT | Ethereum (ERC-20) | `0x5D8b7f8Ec58E03C56B806B11A52eC2c6D16f5a1d` |
| USDT | TON | `UQAq90X97DbxDJ_6B9IU14qBE5W4cGvjWNUTFIjybPRsPEp1` |
| USDT | Solana | `5GFPaxnYVS1vgg8qJCvWKUiVSjmAAiYNhFSGLikT3SWq` |
| TRX | TRON | `TJ3hyPm5fFWrf3XqbaL4C5TTWVjf3TP7sw` |
| SOL | Solana | `5GFPaxnYVS1vgg8qJCvWKUiVSjmAAiYNhFSGLikT3SWq` |

> **Важно:** перед отправкой обязательно проверьте валюту, адрес и выбранную
> сеть. Перевод через несовместимую сеть может привести к потере средств.

## Возможности

- перевод с английского на русский;
- захват только выбранного окна, без чтения рабочего стола;
- полупрозрачные карточки перевода поверх исходного текста;
- настраиваемый и запоминаемый размер шрифта;
- глобальные горячие клавиши;
- работа без Python, PaddleOCR и OpenCV.

## Установка

1. Скачайте архив `EasyGameTranslator-0.1-Beta-win-x64.zip` на странице
   [Releases](https://github.com/VaporWaveXReal/EasyGameTranslator/releases).
2. Распакуйте архив в отдельную папку.
3. Запустите `EasyGameTranslator.exe`.

Для запуска требуется Windows 10 версии 2004 или новее либо Windows 11 и
[.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0).
Для OCR должен быть установлен английский языковой пакет Windows.

## Использование

1. Откройте игру или браузер в оконном либо безрамочном режиме.
2. Запустите `EasyGameTranslator.exe`.
3. Выберите окно и размер шрифта.
4. Нажмите «Запустить перевод».

Эксклюзивный полноэкранный режим не поддерживается.

Глобальные горячие клавиши:

- `F7` — запустить перевод;
- `F6` — перезапустить захват и перевод выбранного окна;
- `F8` — остановить перевод и открыть настройки.

## Сборка из исходного кода

Установите [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), затем:

```powershell
dotnet restore
dotnet build -c Release
```

## Как предложить изменение

Сообщения об ошибках и предложения можно создавать во вкладке **Issues**.
Изменения кода принимаются через pull request: сделайте fork, создайте отдельную
ветку и отправьте PR. Подробности находятся в [CONTRIBUTING.md](CONTRIBUTING.md).
Автор проекта проверяет каждый pull request и решает, принять его или отклонить.

## Лицензия

Проект распространяется по лицензии [MIT](LICENSE).
