# API кабинета магазина

Единственный источник правды по решениям и API кабинета магазина. Новые шаги реализации должны обновлять этот документ; существующие маршруты сохраняются ради обратной совместимости.

## Правила

- `accountType`: `Private | Business`. Выставляется только сервером (приглашение продавца или включение бизнеса пользователем). Роль `Owner` = владелец платформы, не владелец магазина.
- Приглашённый магазин полноценный бизнес-аккаунт без реквизитов. Реквизиты нужны только для подачи на верификацию.
- Знаки доверия выдаёт только сервер, клиент не может их передать ни в одном DTO.
- Все `/api/shop/*` требуют аутентификации и `AccountType.Business`. `OwnerId` берётся только из текущего пользователя. Чужой и отсутствующий ресурс = 404.
- Данные, которые нельзя честно посчитать, возвращаются как `null`.
- Миграции только аддитивные, старые маршруты не удалять (`api/account/business`, публичные профили по GUID и пятизначному коду, существующие `/api/me/2fa` и `/api/me/sessions` остаются).
- Валюта: рубли ПМР.

### Контракты и маршруты

#### Аккаунт

`GET /api/me` возвращает существующий приватный профиль с добавленными полями:

```json
{
  "accountType": "personal",
  "canDisableBusiness": false
}
```

`accountType` — строка `personal` для `Private`, `shop` для `Business`. Пятизначный `publicCode` остаётся пятизначным; клиент отображает его с префиксом `GEN-`. `canDisableBusiness` истинно только для самостоятельно включённого бизнеса; у приглашённого магазина оно ложно. Другие поля `/api/me` не меняются.

Существующие маршруты сохраняются: `GET /api/me/2fa`, `POST /api/me/2fa/setup`, `POST /api/me/2fa/enable`, `POST /api/me/2fa/disable`, `POST /api/me/2fa/recovery-codes`, `GET /api/me/sessions`, `DELETE /api/me/sessions/{id}`. Город сессии равен `null`.

`GET /api/me/2fa` отвечает `{ "enabled": false, "setupPending": false, "required": false, "recoveryCodesLeft": 0 }`. `POST /api/me/2fa/setup` не имеет тела и выдаёт `{ "secret": "…", "provisioningUri": "otpauth://…" }`; повторная настройка при включённой 2FA — `409`. `POST /api/me/2fa/enable`, `/disable` и `/recovery-codes` принимают `{ "code": "…" }`. Включение отвечает `{ "recoveryCodes": ["…"], "accessToken": "…", "accessTokenExpiresAt": "…" }`; перевыпуск отвечает `{ "recoveryCodes": ["…"] }`; отключение — `204`. Неверный код — `400`, превышение лимита проверки — `429`; служебному аккаунту отключение запрещено с `403`.

`GET /api/me/sessions` отвечает массивом `{ "id", "deviceFamily", "browserFamily", "osFamily", "ipPrefix", "city": null, "createdAt", "lastSeenAt", "isCurrent" }`. `DELETE /api/me/sessions/{id}` отзывает только собственную сессию и отвечает `204`; неизвестная/чужая — `404`. Текущий access token действует до истечения срока, но refresh-токен сессии отзывается.

Настройки аккаунта доступны владельцу как personal-, так и shop-аккаунта.

`POST /api/account/email-change/send-code` принимает `{ "newEmail": "new@example.com" }`. Адрес обрезается и приводится к нижнему регистру; текущий адрес отклоняется с `409 email_same_as_current`. При свободном и занятом адресе возвращается одинаковый `200 { "message": "Если адрес можно использовать, код отправлен на него." }`; для занятого адреса письмо не отправляется. Для свободного адреса код отправляется на новый email. Повторная отправка ограничена общим cooldown кодов подтверждения.

`POST /api/account/email-change/verify` принимает `{ "newEmail": "new@example.com", "code": "123456" }`. Успех: `{ "email": "new@example.com", "emailVerified": true, "accessToken": "…", "accessTokenExpiresAt": "…" }`. Новый email сразу подтверждается; код одноразовый. Сервер отзывает остальные сессии и refresh-токены, сохраняет текущую refresh-сессию, выдаёт ей новый access token и отправляет security-письмо на старый адрес. Если адрес заняли после отправки кода — `409 email_already_in_use`; параллельное изменение профиля — `409 email_change_conflict`.

`GET /api/account/notification-preferences` возвращает `{ "groups": [{ "key": "activity", "description": "…", "enabled": true, "canDisable": true, "types": ["…"] }], "notifyVia": "Email" }`. Настройки применяются поверх существующего канала `Profile.NotifyVia`. `PUT` принимает только изменяемые настройки, например `{ "groups": { "activity": false, "reviews": true }, "notifyVia": "Telegram" }`; пропущенные группы не меняются. Неизвестный ключ даёт `400 unknown_notification_group`, попытка передать `security` — `400 security_group_managed_by_server`, неизвестный канал — `400 invalid_notification_channel`. Без строки настройки группа включена. Письмо-подтверждение получения обратной связи учитывает настройку `feedback`, только если email отправителя совпадает с email аккаунта; уведомление администратору не зависит от неё.

Группы уведомлений:

| Ключ | Смысл и включённые типы | Можно отключить |
|---|---|---:|
| `security` | Коды подтверждения email и входа, восстановление пароля, уведомление о смене email, подтверждение удаления. Сервер сейчас не отправляет отдельные оповещения о новом устройстве или сбросе 2FA; когда появятся, они относятся сюда. | Нет |
| `activity` | Объявление одобрено/отклонено/возвращено на доработку, предупреждение об автоархивации, принятие приглашения магазина. | Да |
| `reviews` | Новый отзыв о продавце. | Да |
| `saved_searches` | Новые объявления по сохранённому поиску. | Да |
| `feedback` | Подтверждение получения обращения отправителю, когда его email совпадает с email аккаунта. | Да |

`POST /api/account/delete/send-code` отправляет одноразовый код на текущий email и отвечает `{ "message": "Код отправлен на email аккаунта.", "expiresAt": "…" }`. `POST /api/account/delete/confirm` принимает `{ "code": "123456", "twoFactorCode": "123456" }`; `twoFactorCode` требуется только при включённой 2FA и может быть TOTP либо recovery-кодом. Успех — `204 No Content`. Неверный код: `400 account_delete_code_invalid`, истёкший: `410 account_delete_code_expired`, исчерпаны попытки: `429 account_delete_code_attempts_exceeded`, отсутствует активный код: `400 account_delete_code_not_found`, отсутствует обязательный второй фактор: `400 second_factor_required`, неверен второй фактор: `400 invalid_second_factor`. Старый `DELETE /api/me` сохранён, но всегда отвечает `409 confirmation_required`; он не удаляет аккаунт без подтверждения.

Дополнительные коды: для отправки email-кода слишком часто — `429 verification_code_cooldown` с `Retry-After`; временно недоступна почта — `503 email_delivery_unavailable`; некорректный новый адрес — `400 invalid_email`; запрос смены на текущий адрес — `409 email_same_as_current`; занятие адреса после отправки — `409 email_already_in_use`; параллельная смена аккаунта — `409 email_change_conflict`; отсутствие session id при смене — `409 session_refresh_required`. Для удаления блокировка проверки 2FA отвечает `429 second_factor_locked`. Все ошибки возвращают `application/problem+json` с полем `code`.

Анонимизация shop-аккаунта удаляет бизнес-профиль и настройки витрины, архивирует активные объявления, сохраняет отзывы за обезличенным пользователем; публичный профиль магазина больше не выдаётся.

#### Бизнес-аккаунт и организация

Сохраняются `GET|PUT /api/account/business`, `POST /api/account/business/submit` и `POST /api/account/business/disable`.

`PUT /api/account/business` принимает название магазина и необязательный комплект организации:

```json
{
  "shopName": "Лавка у рынка",
  "shopCity": "Tiraspol",
  "legalForm": "IndividualEntrepreneur",
  "registrationNumber": "12345",
  "pickupAddress": "Центр, дом 1"
}
```

Организационные поля передаются либо все вместе, либо все отсутствуют. Само включение бизнеса не требует организации. Ответ содержит обратносуместимое `details` и новое поле `organization`: `null`, если комплект не заполнен; иначе `{ "legalForm", "registrationNumber", "pickupAddress" }`. Название организации не предусмотрено.

`DELETE /api/account/business/organization` очищает комплект организации, не выключая бизнес. У проверенного бизнеса очистка сбрасывает статус на `None`; ответ — текущее представление бизнес-аккаунта и `verificationReset: true`. При отсутствии комплекта очистка идемпотентна. Пока заявка имеет статус `Pending`, операция завершается `409 details_locked`.

Пример ответа `GET /api/account/business` без заполненных реквизитов и ответа очистки:

```json
{
  "accountType": "Business",
  "isInvitedStore": true,
  "shopName": "Лавка у рынка",
  "shopCity": "Tiraspol",
  "status": "None",
  "details": {
    "shopName": "Лавка у рынка",
    "legalForm": null,
    "registrationNumber": null,
    "pickupAddress": null
  },
  "organization": null,
  "canEdit": true,
  "canSubmit": false,
  "nextSubmitAllowedAt": null,
  "verificationReset": false
}
```

После очистки непустой организации `verificationReset` равен `true`, если до очистки аккаунт был проверен; `status` тогда `None`, а `accountType` остаётся `Business`. У самостоятельного бизнеса `isInvitedStore` равен `false`.

`POST /api/account/business/disable` разрешён для самостоятельно включённого бизнеса. Для приглашённого магазина ответ `409` с `code: "invited_store_cannot_disable"`.

```json
{
  "title": "Магазинный аккаунт, созданный по приглашению, нельзя отключить",
  "status": 409,
  "code": "invited_store_cannot_disable"
}
```

В клиентских DTO записи организации отсутствуют `status`, `isVerifiedBusiness`, `trustSignals` и иные управляющие доверительными знаками поля. Публичный профиль выдаёт реквизиты и знак «Реквизиты проверены» только если комплект организации существует и статус `Verified`. Почта может давать только серверный знак «Проверенный пользователь».

#### Реализованные маршруты кабинета магазина

Все эти маршруты требуют аутентификации и собственного `Business` аккаунта. При работе с ресурсом по ID сервер ограничивает выборку текущим `OwnerId`; чужой и отсутствующий ресурс возвращаются как 404.

| Метод и путь | Назначение |
|---|---|
| `GET /api/shop/me` | Сведения о своём магазине и публичный код |
| `GET /api/shop/overview` | Счётчики, ContactReveal статистика, чек-лист и превью |
| `GET /api/shop/listings` | Список объявлений с фильтрами и пагинацией |
| `POST /api/shop/listings/bulk-publish` | Публикация до 100 выбранных объявлений |
| `POST /api/shop/listings/bulk-bump` | Поднятие до 100 объявлений с индивидуальной проверкой ограничений |
| `POST /api/shop/listings/bulk-archive` | Архивация до 100 объявлений |
| `POST /api/shop/listings/bulk-delete` | Удаление до 100 объявлений |
| `GET|PUT /api/shop/pickup-points` | До 5 точек самовывоза, ровно одна основная |
| `GET|PUT /api/shop/links` | Instagram и сайт магазина |

Эти маршруты доступны и приглашённому магазину без реквизитов.

#### Маршруты следующих шагов

| Метод и путь | Назначение |
|---|---|
| `GET|PUT /api/shop/showcase` | Настройки витрины |
| `PUT /api/shop/showcase/pinned-listings` | До 3 закреплённых товаров и их порядок |
| `POST|DELETE /api/shop/media/banner` | Загрузка/удаление баннера; логотип использует аватар |
| `GET /api/shop/reviews` | Отзывы и сводка; нерассчитываемые метрики равны `null` |
| `PUT /api/shop/reviews/{id}/reply` | Один ответ магазина на отзыв |

Массовые запросы принимают `{ "ids": ["<guid>"] }`. Обработка не атомарная: результат содержит по каждому ID `ok` либо причину отказа. Поля доверия и `OwnerId` клиентом не задаются.

Запрос закреплений принимает `{ "listingIds": ["<guid>", "<guid>", "<guid>"] }`; порядок массива задаёт порядок на витрине, пустой массив снимает все закрепления. Ответы массовых операций имеют форму `{ "items": [{ "id": "<guid>", "ok": true, "reason": null }] }`; каждый ответ содержит ровно одну запись на входной ID. Для неуспешной записи `ok` равен `false`, `reason` содержит стабильный код причины. Запрос свыше 100 ID отклоняется с `400 bulk_limit_exceeded`.

Настройки витрины принимают только поддерживаемые значения сортировки `new | price_asc`. Контакты используют существующие поля профиля (телефон, Telegram username, флаги Viber/WhatsApp). Описание использует `Profile.Description` (до 300 символов), логотип — аватар. Новые поля витрины: баннер, Instagram и сайт. Instagram и сайт записываются через `PUT /api/shop/links`; «Условия получения» — необязательный текст до 500 символов в `PUT /api/shop/pickup-points`.

Точки самовывоза — новая сущность, не более пяти на магазин, ровно одна основная. При переносе старого `PickupAddress` он становится адресом основной точки; существующая подача бизнес-реквизитов на проверку продолжает работать.

#### Коды ошибок

Бизнес-операции возвращают `application/problem+json` с машинным `code`. Значимые коды:

| HTTP | `code` | Значение |
|---:|---|---|
| 400 | `validation_failed` | Ошибка проверки запроса; подробности в `errors` и `codes` |
| 400 | `pickup_points_limit`, `pickup_main_required`, `pickup_main_inactive`, `invalid_city`, `pickup_address_required`, `pickup_address_too_long`, `pickup_note_too_long`, `invalid_pickup_hours`, `invalid_pickup_break`, `invalid_conditions_note`, `conditions_note_too_long`, `duplicate_pickup_point_id` | Некорректный список точек самовывоза или условия получения |
| 400 | `invalid_instagram`, `invalid_website`, `invalid_telegram_username` | Неверная ссылка магазина или Telegram username |
| 400 | `legal_form_required`, `registration_number_required`, `registration_number_format`, `pickup_address_required` | Некорректный/неполный комплект для верификации |
| 403 | `email_not_verified` | Для подачи на верификацию нужно подтвердить email |
| 404 | — | Ресурс не найден или принадлежит другому пользователю |
| 409 | `invited_store_cannot_disable` | Приглашённый магазин не может отключить бизнес |
| 409 | `details_locked` | Организация на проверке и доступна только для чтения |
| 409 | `concurrent_change` | Состояние изменилось параллельно; перечитать ресурс |
| 409 | `already_pending`, `already_verified`, `registration_number_taken` | Конфликт состояния/данных |
| 429 | `resubmit_cooldown` | Повторная подача пока запрещена; ответ включает `retryAt` |

#### Подробные контракты реализованных маршрутов

Все маршруты ниже требуют Bearer-аутентификацию и `Business`; для `Private` возвращается `403 business_account_required`, а анонимный запрос получает `401`. Владелец всегда берётся из текущего пользователя. Ресурс по чужому ID неотличим от отсутствующего и возвращает `not_found`.

`GET /api/shop/me` возвращает `publicCode` из пяти цифр, `shopName`, `city`, `avatarUrl`, `description`, `isInvitedStore`, `listingsCount`, `favoritesCount`, `ratingAverage`, `reviewsCount`, `trustBadges`, `hasOrganization` и `pickupPointsCount`. Счётчик точек включает активные и неактивные точки. `listingsCount` включает все неудалённые объявления владельца; `favoritesCount` — избранное самого владельца аккаунта. При отсутствии отзывов `ratingAverage` и `reviewsCount` равны `null`. Бейджи с ключами `email_verified` и `business_verified` формирует только сервер; подтверждённая почта показывает «Проверенный пользователь», а второй бейдж доступен только для `Verified` с полным комплектом реквизитов.

`GET /api/shop/overview` отвечает формой:

```json
{
  "counts": { "all": 8, "draft": 2, "review": 1, "active": 3, "sold": 1, "archived": 1, "rejected": 0 },
  "withoutPhotoCount": 2,
  "contactReveals": { "total": 40, "last7d": 7, "last30d": 18 },
  "checklist": [
    { "key": "logo", "done": true },
    { "key": "publish_drafts", "done": false },
    { "key": "pickup_address", "done": false },
    { "key": "working_hours", "done": false },
    { "key": "messengers", "done": true },
    { "key": "photos", "done": false }
  ],
  "previewListings": [
    { "id": "<guid>", "title": "Товар", "price": 250, "priceType": "Fixed", "thumbnailUrl": "https://…" }
  ]
}
```

Счётчики объявлений не включают мягко удалённые записи. `withoutPhotoCount` считает объявления `draft`, `review` (`PendingReview`) и `active` без изображений. `contactReveals` считает записи журнала раскрытия контактов объявлений магазина за всё время и за скользящие 7/30 дней. Это число событий раскрытия, а не уникальных людей: повторные открытия тоже учитываются. В чек-листе `logo` означает существующий аватар; `publish_drafts` — отсутствие черновиков; `pickup_address` — наличие активной точки, либо непустой legacy `PickupAddress`, если точек ещё нет; `messengers` — Telegram username или флаг Viber/WhatsApp; `photos` — нулевой `withoutPhotoCount`. `working_hours` равен `true`, если у основной точки заданы часы, иначе `false`. У приглашённого магазина без реквизитов остальные пункты считаются так же.

`previewListings` содержит до трёх последних активных объявлений; в шаге 5 закреплённые объявления будут идти первыми. `thumbnailUrl` — публичное превью изображения, `null`, если фото отсутствует.

#### Точки самовывоза

`GET /api/shop/pickup-points` читает список владельца. `PUT /api/shop/pickup-points` полностью заменяет список: существующие `id` обновляются, записи без `id` создаются, отсутствующие в теле удаляются. Чужой или неизвестный `id` даёт `404 not_found`. Массив может быть пустым; иначе требуется ровно одна активная основная точка. Максимум — пять записей. При конкурентной замене сервер отвечает `409 concurrent_change`.

Пример тела `PUT` (в ответе у каждой точки есть выданный сервером `id` и вычисленные поля состояния):

```json
{
  "points": [{
    "city": "Tiraspol",
    "address": "ул. Ленина, 10",
    "note": "Вход со двора",
    "isMain": true,
    "isActive": true,
    "hours": {
      "days": [
        { "day": 1, "closed": false, "from": "09:00", "to": "18:00" },
        { "day": 2, "closed": false, "from": "09:00", "to": "18:00" },
        { "day": 3, "closed": false, "from": "09:00", "to": "18:00" },
        { "day": 4, "closed": false, "from": "09:00", "to": "18:00" },
        { "day": 5, "closed": false, "from": "09:00", "to": "18:00" },
        { "day": 6, "closed": false, "from": "10:00", "to": "16:00" },
        { "day": 7, "closed": true, "from": null, "to": null }
      ],
      "breakFrom": "13:00",
      "breakTo": "14:00"
    }
  }],
  "conditionsNote": "Самовывоз после подтверждения заказа"
}
```

Успешный ответ `GET`/`PUT`:

```json
{
  "points": [{
    "id": "<guid>", "city": "Tiraspol", "address": "ул. Ленина, 10",
    "note": "Вход со двора", "isMain": true, "isActive": true,
    "hours": null, "openNow": null, "closesAt": null, "opensAt": null
  }],
  "conditionsNote": "Самовывоз после подтверждения заказа"
}
```

У `hours.days` должны быть ровно семь уникальных дней: 1 — понедельник, 7 — воскресенье. Для каждого открытого дня нужны `HH:mm` и `from < to`. `breakFrom` и `breakTo` задаются парой, а весь перерыв должен лежать внутри рабочего времени каждого открытого дня. При `hours: null` `openNow`, `closesAt` и `opensAt` равны `null`. Иначе сервер вычисляет их в часовом поясе `Europe/Chisinau`: `openNow` всегда boolean, `closesAt` заполняется при открытой точке, `opensAt` — когда она закрыта и есть следующее время открытия. Во время перерыва точка закрыта, а `opensAt` равно `breakTo`.

`conditionsNote` — до 500 символов. Если ключ опущен, значение не меняется; `null` или пустая строка очищает его. `address` обязателен (до 200 символов), `note` необязателен (до 200), `city` должен существовать в справочнике городов проекта.

Коды ошибок: `400 pickup_points_limit`, `pickup_main_required`, `pickup_main_inactive`, `invalid_city`, `pickup_address_required`, `pickup_address_too_long`, `pickup_note_too_long`, `invalid_pickup_hours`, `invalid_pickup_break`, `invalid_conditions_note`, `conditions_note_too_long`, `duplicate_pickup_point_id`; `403 business_account_required`; `404 not_found`; `409 concurrent_change`.

При миграции существующего `PickupAddress` создаётся одна активная основная точка, если у владельца ещё нет точек. Город — `ShopCity`, а если он равен `null`, `Profile.City`. Если заполненный `PickupAddress` не имеет города ни в `ShopCity`, ни в `Profile.City`, миграция завершается ошибкой, не пропуская адрес молча. До применения миграции проверьте такие записи:

```sql
SELECT bp."UserId", bp."PickupAddress", bp."ShopCity", p."City" AS "ProfileCity"
FROM business_profiles bp
LEFT JOIN profiles p ON p."UserId" = bp."UserId"
WHERE NULLIF(btrim(bp."PickupAddress"), '') IS NOT NULL
  AND COALESCE(bp."ShopCity", p."City") IS NULL
  AND NOT EXISTS (
      SELECT 1 FROM shop_pickup_points point WHERE point."OwnerId" = bp."UserId"
  );
```

Колонка `PickupAddress` остаётся независимой частью комплекта реквизитов и по-прежнему участвует в текущей подаче на верификацию; синхронизации с точками нет.

#### Ссылки магазина

`GET /api/shop/links` возвращает `{ "instagram": "shop_name", "website": "https://example.md" }`. `PUT /api/shop/links` заменяет оба значения; пустое значение или `null` очищает поле:

```json
{
  "instagram": "https://www.instagram.com/shop_name/",
  "website": "example.md/catalog"
}
```

Успешный ответ содержит нормализованный Instagram username без `@` и домена (1–30 букв, цифр, точек и подчёркиваний) и URL сайта. Для сайта сервер добавляет `https://`, если схема не указана; допускаются только `http` и `https`, длина — до 200 символов. Ошибки: `400 invalid_instagram`, `400 invalid_website`, `403 business_account_required`.

#### Дополнение публичного профиля магазина

Существующие публичные профили по GUID и пятизначному коду сохраняют все прежние поля. Аддитивно возвращаются `pickup` и `links`. `pickup` содержит только активные точки, основная идёт первой. Если активных точек нет, `pickup: null`. `links` для Business-профиля — объект, даже если оба поля пусты; для частного профиля — `null`.

```json
{
  "pickup": {
    "points": [{
      "city": "Tiraspol",
      "address": "ул. Ленина, 10",
      "note": null,
      "hours": null,
      "openNow": null
    }],
    "conditionsNote": "После подтверждения заказа"
  },
  "links": { "instagram": "shop_name", "website": "https://example.md" }
}
```

`GET /api/shop/listings` принимает `status=draft|review|active|sold|archived|rejected`, строку поиска `q`, `sort=new|price_asc`, `page` (с 1) и `pageSize` (по умолчанию 20, максимум 50). Неизвестные значения и page/pageSize меньше 1 дают `400 validation_failed`. Для `price_asc` пустая цена идёт после заполненных; внутри равной цены порядок — сначала новые. Ответ:

```json
{
  "items": [{
    "id": "<guid>", "publicCode": null, "title": "Товар", "category": "home",
    "price": 250, "priceType": "Fixed", "status": "active", "city": "Tiraspol",
    "createdAt": "…", "archivesAt": "…", "hasPhoto": true, "thumbnailUrl": "https://…"
  }],
  "total": 3, "page": 1, "pageSize": 20,
  "counts": { "all": 8, "draft": 2, "review": 1, "active": 3, "sold": 1, "archived": 1, "rejected": 0 }
}
```

У объявления нет отдельного пятизначного публичного кода, поэтому `publicCode` пока `null`. `archivesAt` вычисляет сервер для `active` как `coalesce(BumpedAt, PublishedAt, CreatedAt) + CatalogHygiene:ArchiveAfterDays`; для остальных статусов он `null`. `review` соответствует сохранённому статусу `PendingReview`. Постмодерация сохраняет фактический `active` и не меняет вкладку состояния объявления.

Массовые запросы принимают `{ "ids": ["<guid>"] }`. Пустой список — `400 validation_failed`; больше 100 элементов запроса — `400 bulk_limit_exceeded`. Дубликаты схлопываются, ответ содержит ровно одну запись на уникальный ID в порядке первого появления:

```json
{
  "items": [
    { "id": "<guid>", "ok": true, "reason": null, "status": "active", "retryAt": null },
    { "id": "<guid>", "ok": true, "reason": "moderation_required", "status": "review", "retryAt": null },
    { "id": "<guid>", "ok": false, "reason": "bump_cooldown", "status": "active", "retryAt": "…" },
    { "id": "<guid>", "ok": true, "reason": null, "status": null, "retryAt": null }
  ]
}
```

Доступные операции: `POST /api/shop/listings/bulk-publish`, `bulk-bump`, `bulk-archive`, `bulk-delete`. Действия не атомарны: отказ одного ID не отменяет остальные. Стабильные причины: `not_found`, `invalid_status`, `missing_requirements`, `bump_cooldown`, `moderation_required`. `moderation_required` — успешная публикация, ожидающая решения модератора: при премодерации сохранённый статус `review`; при постмодерации возвращается фактический статус `active` вместе с причиной `moderation_required`. Отсутствие фотографии само по себе не запрещает публикацию: применяется та же оценка риска и модерация, что и в одиночном маршруте. `retryAt` задаётся только для `bump_cooldown` и использует `CatalogHygiene:BumpCooldownDays`. `bulk-delete` выполняет мягкое удаление через `DeletedAt`, сохраняет строку и историю, скрывает её обычным query-фильтром и возвращает `status: null`; активное объявление перед скрытием переводится в архив для снятия с публикации в Telegram.

#### Существующие маршруты, используемые кабинетом

| Назначение | Метод и путь | Тело | Успешный ответ |
|---|---|---|---|
| Название магазина и город | `PUT /api/account/business` | `{ "shopName": "Лавка", "shopCity": "Tiraspol" }` (организационные поля можно опустить) | Бизнес-представление с `shopName`, `shopCity`, `details`, `organization`, `status` |
| «О себе», контакты и мессенджеры | `PATCH /api/me` | `{ "description": "…", "phoneE164": "+373…", "telegramUsername": "shop", "viberEnabled": false, "whatsappEnabled": true }` | Обновлённый приватный профиль; возвращаются `description`, `phoneE164`, `telegramUsername`, `viberEnabled`, `whatsappEnabled` |
| Загрузить логотип | `POST /api/me/avatar` multipart/form-data, поле `file` | Файл JPEG/JPG, PNG, WebP или HEIC/HEIF до 5 МБ | `{ "avatarUrl": "https://…" }` |
| Удалить логотип | `DELETE /api/me/avatar` | Нет | `204 No Content` |
| Создать объявление | `POST /api/listings` | `CreateListingRequest`: `title`, `description`, `price`, `priceType`, `category`, `subcategoryId`, `city`, `district`, `condition`, `publish` | `201 Created` и `ListingResponse` |
| Изменить объявление | `PATCH /api/listings/{id}` | `UpdateListingRequest`: заголовок, описание, цена/тип, категория, подкатегория, город, район, состояние | `200 OK` и `ListingResponse` |
| Снять с публикации / архивировать | `DELETE /api/listings/{id}` | Нет | `204 No Content` |
| Опубликовать черновик | `POST /api/listings/{id}/publish` | Нет | `200 OK` и `ListingResponse`; может перейти в `PendingReview` |
| Поднять | `POST /api/listings/{id}/bump` | Нет | `200 OK` и `ListingResponse`; cooldown отвечает `429` |
| Отметить проданным | `POST /api/listings/{id}/mark-sold` | Нет | `200 OK` и `ListingResponse` |

`PATCH /api/me` валидирует телефон через `PhoneNumber.Normalize` с учётом `Phone:AllowOtherCountries`; при смене номера сбрасывается `phoneVerified`. Если передан `telegramUsername`, сервер удаляет ведущий `@` или `t.me/`, затем требует 5–32 символа из букв, цифр и `_`; иначе возвращает `400 invalid_telegram_username`. Не переданный username остаётся как есть: старые сохранённые значения не перепроверяются. `description` нормализуется и ограничена 300 символами. Чужое объявление для `PATCH` и действий владельца возвращает `404`; старые маршруты сохраняются и используются кабинетом.

#### Контакты магазина для кабинета

Фронт читает контакты магазина вместе с приватным профилем через `GET /api/me`. Значимые поля: `phoneE164`, `phoneVerified`, `telegramUsername`, `viberEnabled`, `whatsappEnabled`, `showPhoneInListing`. Запись использует частичное обновление `PATCH /api/me`, например:

```json
{
  "phoneE164": "+373…",
  "telegramUsername": "shop_name",
  "viberEnabled": true,
  "whatsappEnabled": false,
  "showPhoneInListing": true
}
```

Ответ содержит те же нормализованные значения; при смене телефона `phoneVerified` становится `false`. Магазинных контактов отдельно нет.

Покупатель получает контакты только через `GET /api/listings/{id}/contact`. Ответ содержит `phone`, `telegramUrl`, `viberUrl`, `whatsappUrl`; когда контактов для показа нет, endpoint отвечает `404`. `showPhoneInListing` управляет телефоном. Viber и WhatsApp дополнительно требуют включённого соответствующего флага и видимого телефона, поскольку deeplink раскрывает номер. Telegram username показывается независимо от `showPhoneInListing`, если он задан. Отдельных флагов видимости телефона, Telegram, Viber и WhatsApp по каналам нет; существуют общий флаг показа телефона и флаги доступности Viber/WhatsApp. Для авторизованного покупателя подтверждение email обязательно; событие раскрытия записывается в `contact_reveals`.

## Решения

1. business/disable: запрещён (409) для приглашённых магазинов, разрешён для аккаунтов, которые сами включили бизнес. Сервер отдаёт canDisableBusiness.
2. Контакты магазина = существующие поля профиля (телефон, Telegram username, флаги Viber/WhatsApp). Отдельных магазинных контактов нет.
3. Логотип = существующий аватар. Описание = существующее "О себе" (300). Новые поля только: баннер, Instagram, сайт.
4. Точки самовывоза: новая сущность, максимум 5, ровно одна основная; старый PickupAddress мигрирует в основную точку; подача на верификацию не ломается.
5. Не реализуем: доставка на такси, рейсовый транспорт, оплата картами, заявки через Telegram-бот, приём заказов вне графика. Вместо них одно необязательное поле "Условия получения" (до 500 символов).
6. Метрики отзывов (рекомендуют, скорость ответа, точность описания, упаковка) = null. Фото в отзывах не делаем. Ответ продавца делаем (один на отзыв).
7. Группы уведомлений = только те типы, которые сервер реально отправляет; системные по безопасности отключить нельзя.
8. Город сессии остаётся null.
9. Данные организации: форма + регистрационный номер + адрес только комплектом (текущее правило). Поля "название организации" нет. Добавить возможность очистить данные организации, не отключая бизнес.
10. Тексты знаков доверия задаёт сервер. Подтверждённая почта даёт "Проверенный пользователь"; "Реквизиты проверены" только после проверки модератором.
11. Количество избранного в меню = избранное самого пользователя.
12. На витрине закрепляется максимум 3 товара.
13. Массовые действия не атомарные: ответ по каждому ID (ok или причина), максимум 100 ID.
14. Сортировка витрины: new | price_asc (без "популярного").
