# Genesis Market — подтверждение почты по коду

Задание для Claude Code. Шаблоны писем лежат рядом:
`verification-code.html` и `verification-code.txt`.
Плейсхолдеры: `{{CODE}}`, `{{LOGO_URL}}`.

## 1. Хранение

Таблица `email_verification_codes`:

| поле | тип | назначение |
|---|---|---|
| `id` | uuid PK | |
| `email` | text, нормализован в lowercase + trim | |
| `code_hash` | text | хеш кода, **не сам код** |
| `expires_at` | timestamptz | now() + 5 минут |
| `attempts` | int, default 0 | счётчик неверных вводов |
| `consumed_at` | timestamptz null | отметка использования |
| `created_at` | timestamptz | для rate limiting |

Индексы: `(email, created_at desc)`, `(expires_at)`.

Код в базе хранить нельзя в открытом виде. Утечка дампа = чужие аккаунты.
Хешировать HMAC-SHA256 с серверным ключом из конфигурации (не Bcrypt —
он медленный, а здесь нужен быстрый lookup; энтропии 6 цифр мало, но
секретный ключ HMAC закрывает перебор по украденному дампу).

## 2. Генерация кода

- Только криптографический ГСЧ (`RandomNumberGenerator`, не `Random`).
- 6 цифр, диапазон 000000–999999, ведущие нули сохраняются
  (форматировать как строку с padding, иначе «042917» станет «42917»).
- При выпуске нового кода все прежние неиспользованные коды
  для этого email пометить как `consumed_at = now()`.

## 3. Отправка

- Rate limit: не чаще 1 письма в 60 секунд на email,
  не больше 5 писем в час на email, не больше 20 в час на IP.
- Ответ эндпоинта `POST /auth/send-code` **всегда одинаковый**,
  независимо от того, зарегистрирован адрес или нет, и независимо от
  того, сработал ли rate limit. Иначе эндпоинт превращается в
  проверялку «есть ли такой пользователь».

## 4. Проверка

`POST /auth/verify-code` с `{ email, code }`:

1. Взять последний неиспользованный код для email.
2. Если `attempts >= 5` — отказ, код сжечь.
3. Если `expires_at < now()` — отказ.
4. Сравнить хеши в постоянном времени (`CryptographicOperations.FixedTimeEquals`).
5. Неверно → `attempts += 1`, вернуть общую ошибку.
6. Верно → `consumed_at = now()`, выдать токен.

Текст ошибки один на все случаи: «Неверный или устаревший код».
Не разделять «код не тот» и «код истёк» — это подсказка атакующему.

## 5. Уборка

Фоновая задача раз в час: удалять записи старше суток.

## 6. Отправка письма

Интерфейс `IEmailSender` с одним методом:

```csharp
Task SendVerificationCodeAsync(string email, string code, CancellationToken ct);
```

Реализация `GmailSmtpEmailSender`:
- MailKit (`SmtpClient` из `MailKit.Net.Smtp`), не `System.Net.Mail`.
- `smtp.gmail.com:587`, `SecureSocketOptions.StartTls`.
- Логин — адрес Gmail, пароль — App Password (16 символов),
  из user-secrets в dev и из переменных окружения в проде.
- Письмо multipart: `BodyBuilder` с `HtmlBody` и `TextBody` одновременно.
- From: `new MailboxAddress("Genesis Market", <адрес>)`.
- Subject: `$"{code} — код подтверждения Genesis Market"`.

Шаблон читать один раз при старте и кешировать в памяти,
подстановка — обычный `string.Replace`.

Позже добавится `ResendEmailSender` с тем же интерфейсом;
переключение через конфиг, код вызывающей стороны не меняется.

## Чего делать не нужно

- Не логировать код ни в каком виде.
- Не возвращать код в теле HTTP-ответа даже в dev-режиме.
- Не класть код в JWT или в URL.
