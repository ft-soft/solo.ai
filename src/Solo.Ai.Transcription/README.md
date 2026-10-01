# Транскрибация GigaAM v3

Изолированный срез Solo AI: `POST /api/transcription` → `{ "text": "..." }`.
Не зависит от диалогов, каталога, Visograph или клиентского пакета Solo.Ai.Client.
Работает внутри .NET-процесса: **sherpa-onnx 1.13.8 поверх ONNX Runtime**, CPU.
Подготовку признаков и CTC-декодирование выполняет библиотека, собственного DSP нет.
Python не нужен ни для запросов, ни для установки готового экспорта.

## Запуск

1. Установить FFmpeg в окружение сервера.
2. Скачать и распаковать конкретный экспорт **GigaAM v3 e2e CTC с пунктуацией**:
   [sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-ctc-punct-giga-am-v3-russian-2025-12-16.tar.bz2).
   Это экспорт для sherpa-onnx с метаданными; произвольный upstream ONNX не является заменой.
3. Скачать [Silero VAD](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx)
   в тот же каталог как `silero_vad.onnx`.
4. Добавить в локальный `appsettings.Secrets.json` хоста или передать через environment:

```json
{
  "Transcription": {
    "Enabled": true,
    "ModelPath": "/models/gigaam-v3/model.int8.onnx",
    "TokensPath": "/models/gigaam-v3/tokens.txt",
    "VadModelPath": "/models/gigaam-v3/silero_vad.onnx",
    "FfmpegPath": "ffmpeg",
    "NumThreads": 2
  }
}
```

Без настроек срез выключен (503) и не загружает модель. При включении проверяется
наличие файлов; модель загружается при первом распознавании и переиспользуется.
Первый запрос включает время загрузки модели. Весов в Git нет.
Авторизация — существующая `SoloBackend`: сервисный Bearer token и `X-Solo-User-Id`.
Браузер должен обращаться через backend Solo; сервисный ключ в браузер не передаётся.
Этот срез не добавляет прокси в Solo и не подключает UI автоматически.

```bash
curl --fail-with-body http://localhost:5000/api/transcription \
  -H "Authorization: Bearer $SOLO_BACKEND_KEY" \
  -H "X-Solo-User-Id: $SOLO_USER_ID" \
  -H 'Content-Type: audio/webm' \
  --data-binary @recording.webm
```

Тело запроса — непосредственно байты записи, **не multipart и не base64**.
Допустимы `audio/webm`, `audio/mp4`, `audio/ogg`, `audio/wav`, `audio/x-wav`;
параметр `codecs` в Content-Type допустим. FFmpeg приводит их к mono 16 kHz float PCM.
Пустое распознавание возвращает `200 {"text":""}` — речь может отсутствовать.
Аудио и текст не сохраняются в историю. Временный файл удаляется в finally,
при аварийном завершении процесса может остаться в системном temp.

## Ограничения первой версии

- До **120 секунд**, до **8 MiB** с проверкой потока даже без Content-Length.
  Запись длиннее лимита отклоняется целиком, без возврата обрезанного текста.
- Один запрос на процесс; занято → 429 и `Retry-After: 1`. Очереди нет.
- Бюджет 180 секунд на запрос. Загрузка и FFmpeg отменяются при отключении клиента.
  Между фрагментами проверяется отмена. Нативный вызов текущего фрагмента не прерывается:
  результат отбрасывается после возврата,
  слот удерживается до реального завершения. Это не жёсткий CPU-timeout.
- Записи до 25 секунд распознаются целиком, более длинные разбиваются Silero VAD
  по паузам и обрабатываются последовательно. Тексты соединяются пробелом, без LLM.
  При непрерывной речи срез принудительно завершает фрагмент примерно через 20 секунд
  (с запасом для начального контекста VAD): на такой границе возможно разделение слова.
  По краям VAD-фрагмента сохраняется до 200 мс исходного звука; перекрытие
  не отправляется в модель повторно.
  Предел `MaxSpeechDuration` библиотеки сам по себе не гарантирует жёсткую границу,
  поэтому её обеспечивает SpeechSegmenter. Последний неполный кадр дополнен тишиной
  и сброшен через Flush. Длинная тишина возвращает пустой текст.
- Без потоковой транскрибации, GPU и хранения аудио.

Ошибки возвращаются как `{ "code": "..." }`:

| HTTP | code |
| --- | --- |
| 400 | `empty_audio`, `invalid_audio` |
| 413 | `audio_too_large`, `audio_too_long` |
| 415 | `unsupported_audio_type` |
| 429 | `transcription_busy` |
| 503 | `transcription_disabled`, `transcription_unavailable` |
| 504 | `transcription_timeout` |
| 500 | `transcription_failed` |

401/403 обслуживает существующая авторизация хоста.

## Проверка и удаление

Обычные тесты проверяют авторизацию, выключенный срез, формат, размер, chunked upload,
конкурентный запрос и освобождение слота после отмены без загрузки весов.
Для теста настоящей модели, FFmpeg, повреждённого и слишком длинного аудио:

```bash
SOLO_TRANSCRIPTION_MODEL_DIR=/models/gigaam-v3 \
SOLO_TRANSCRIPTION_FFMPEG=/usr/bin/ffmpeg \
dotnet run --project tests/Solo.Ai.Tests
```

Каталог должен содержать `model.int8.onnx`, `tokens.txt`, `test_wavs/*.wav` из архива и отдельно `silero_vad.onnx`.
Модельная проверка включает ровно 120 секунд с речью в начале, середине и конце,
120 секунд тишины и отклонение 121 секунды.
Без этих переменных модельный тест явно пропускается.

Чтобы удалить фичу: убрать `AddTranscription`/`MapTranscription` и using из хоста,
ProjectReference из хоста/тестов, проект из solution и эту папку с её тестом.
Других изменений в pipeline агента нет.

Источники: [GigaAM](https://github.com/salute-developers/GigaAM),
[экспорт v3 e2e CTC](https://github.com/k2-fsa/sherpa-onnx/blob/master/scripts/nemo/GigaAM/export-onnx-ctc-v3-punct.py),
[C# API sherpa-onnx](https://k2-fsa.github.io/sherpa/onnx/csharp-api/index.html).
