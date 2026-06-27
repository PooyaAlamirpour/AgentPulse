# تصمیم‌ها و پیشنهادهای معماری

## وضعیت سند

این سند هم پیشنهادهای باز تاریخی فاز صفر و هم تصمیم‌های پذیرفته‌شده فازهای اجراشده را نگهداری می‌کند. وضعیت هر مورد در همان بخش مشخص است. پیشنهادهای باز تا زمان ثبت Decision صریح، قرارداد قطعی محسوب نمی‌شوند؛ ADRهای دارای وضعیت `Accepted` بخشی از معماری فعلی پروژه‌اند.

---

## P-001 — نمایش Delta واقعی در Console

- وضعیت: پذیرفته‌شده در فاز ۶
- تصمیم: `IChatModelClient` رویداد `TextDelta` می‌دهد و `IModelOutputSink` هر Delta را فوراً و بدون فاصله یا newline اضافی می‌نویسد. پایان موفق دقیقاً یک newline اضافه می‌کند.
- پیامد: Cancellation یا Failure متن قبلاً نمایش‌داده‌شده را پاک نمی‌کند و stderr از stdout پاسخ مدل جدا می‌ماند.

## P-002 — سیاست Exit Code

- وضعیت: پیشنهاد
- فاز تصمیم‌گیری: ۹
- مسئله: همه شاخه‌های Provider error و Ctrl+C از سورس به Exit Code قطعی و یکسان نمی‌رسند.
- پیشنهاد اولیه: جدول محدود برای success، usage/config، cancellation و runtime/provider error تعریف شود.
- نیازمند تصمیم: کد دقیق هر دسته و parity یا تفاوت عمدی با Node.

## P-003 — نرمال‌سازی Prompt فقط-stdin

- وضعیت: پیشنهاد
- فاز تصمیم‌گیری: ۱ و ۹
- مسئله: Node قبل از stdin یک newline اضافه می‌کند، حتی وقتی Argument خالی است.
- پیشنهاد: محتوای کاربر بدون newline مصنوعی ابتدایی ذخیره شود، ولی ترتیب Argument + newline + stdin برای حالت ترکیبی حفظ شود.
- نیازمند تصمیم: حفظ parity دقیق یا canonicalization ورودی.

## P-004 — نوع Session Run Lock

- وضعیت: پذیرفته‌شده در فاز ۴
- تصمیم: برای هر Session یک Run Lease پایدار در SQLite با کلید یکتای `SessionId` نگهداری می‌شود. Lease دارای شناسه مالک تصادفی، زمان اخذ و زمان انقضا است.
- هم‌زمانی: اخذ Lease همراه با upsert اتمیک Project و تمام تغییرات Run داخل یک Transaction نوشتن انجام می‌شود؛ محدودیت یکتایی دیتابیس مانع Run دوم از DbContext یا Process مستقل می‌شود.
- مالکیت: تمدید و آزادسازی فقط با `LeaseId` مالک مجاز است و مدت Lease از Options خوانده می‌شود.
- پیامد: قفل حافظه‌ای بخشی از قرارداد correctness نیست و SQLite منبع حقیقت Run فعال است.

## P-005 — سیاست Crash Recovery

- وضعیت: پذیرفته‌شده در فاز ۴؛ اثبات Process-level نهایی در فاز ۹
- تصمیم: فقط Lease منقضی‌شده stale محسوب می‌شود. Assistantهای باقی‌مانده در وضعیت `Streaming` به `Failed` منتقل می‌شوند، متن جزئی حفظ و یک دلیل داخلی ثابت و غیرحساس ثبت می‌شود.
- وضعیت Session: Session ابتدا به `Idle` برمی‌گردد، Lease قدیمی حذف می‌شود و سپس در همان Transaction می‌تواند Lease جدید بگیرد و Run جدید را آغاز کند.
- زمان: تشخیص انقضا فقط از `IClock` استفاده می‌کند؛ Lease معتبر هرگز Recovery نمی‌شود.

## P-006 — cadence ذخیره پاسخ ناقص

- وضعیت: پذیرفته‌شده در فاز ۶
- تصمیم: flush بر اساس زمان یا آستانه تعداد کاراکتر انجام می‌شود و روی completion، failure و cancellation یک final flush اجباری وجود دارد. مقادیر پیش‌فرض یک ثانیه و ۲۵۶ کاراکتر هستند.
- پیامد: تعداد Transactionها از تعداد Deltaها کمتر است و متن جزئی در مسیرهای پایان ناموفق حفظ می‌شود.

## P-007 — مدل ذخیره MessagePart قابل توسعه

- وضعیت: پذیرفته‌شده در فاز ۲
- تصمیم: نگاشت TPH روی جدول پایه `MessageParts` با discriminator صریح `PartType`.
- دلیل: قرارداد مشترک شناسه، ترتیب و Timestampها در جدول پایه حفظ می‌شود و افزودن Partهای جدید بدون تغییر رابطه Message و Part ممکن می‌ماند.
- محدودیت فعلی: فقط `TextMessagePart` با مقدار discriminator برابر `text` پیاده‌سازی شده است؛ هیچ Part آینده‌نگرانه دیگری در فاز ۲ ساخته نشده است.
- پیامد Persistence: ستون‌های اختصاصی subtypeها nullable می‌مانند و invariantهای subtype در Domain محافظت می‌شوند.

## P-008 — شناسه پایدار Project برای پوشه Non-Git

- وضعیت: پذیرفته‌شده در فاز ۳
- تصمیم: شناسه `Project` از hash قطعی نسخه‌دارِ مسیر Root canonical و Platform ساخته می‌شود و هیچ مقدار تصادفی یا فایل محلی برای تثبیت شناسه نوشته نمی‌شود.
- رفتار Git: Root همان Working Tree جاری است؛ بنابراین Subdirectoryهای یک Repository شناسه مشترک دارند و Worktreeهای مستقل شناسه متفاوت می‌گیرند.
- رفتار مسیر: در Windows canonicalization نسبت به حروف غیرحساس و با جداکننده یکنواخت است؛ در Unix حروف مسیر حفظ می‌شوند.
- پیامد: جابه‌جایی Root به مسیر دیگر شناسه را تغییر می‌دهد. Resolve کامل تمام symlinkهای میانی در محدوده فاز ۳ نیست.

## P-009 — محل `ModelReference`

- وضعیت: پیشنهاد
- فاز تصمیم‌گیری: ۲ و ۵
- مسئله: Model identity بخشی از Message/Session است، اما parsing و Configuration وابسته به Application/Infrastructure است.
- پیشنهاد: Value Object خنثی و بدون SDK در Domain یا Contracts؛ resolution کامل در Application/Infrastructure.
- placement نهایی باید با dependency direction تست شود.

## P-010 — تفاوت آگاهانه با SDK/Server داخلی Node

- وضعیت: پذیرفته‌شده در فاز ۶
- تصمیم: نسخه .NET مستقیماً `RunPrompt` را از Application اجرا می‌کند و از SDK یا Server داخلی Node استفاده نمی‌کند. Composition Root در CLI قرار دارد و renderer، Credential input، Persistence و HTTP transport از طریق قراردادها سیم‌کشی می‌شوند.
- پیامد: CLI DTOهای Provider یا SQL را مدیریت نمی‌کند و Application به Console، Xiaomi یا Credential Store وابسته نیست.


---

## ADR-001 — ادغام Provider واقعی و Vertical Streaming Flow در فاز ۶

### Context

برنامه اولیه، Streaming آزمایشی، Provider واقعی و اتصال جریان عمودی را بین چند فاز جدا می‌کرد. پیاده‌سازی تأییدشده فاز ۶ این مرز را تغییر داد: دستور `agentpulse run` برای اثبات رفتار واقعی Cancellation، ذخیره متن جزئی، Lease heartbeat و مدیریت Credential باید به Transport واقعی OpenAI-compatible متصل می‌شد. نگه‌داشتن Fake Provider در Runtime هم رفتار امنیتی و هزینه‌ای API واقعی را پنهان می‌کرد و هم Composition Root را با مسیر موقتی ناسازگار می‌ساخت.

### Decision

فاز ۶ شامل Provider واقعی Xiaomi MiMo، HTTP transport سازگار با Chat Completions، SSE streaming، `RunPrompt`، periodic persistence، Lease heartbeat، فرمان‌های مدیریت Credential و جریان عمودی کامل CLI است.

Runtime هیچ Fake Provider ثبت نمی‌کند. تست‌های قطعی Transport و Streaming با HTTP Test Server محلی و پاسخ‌های ازپیش‌تعریف‌شده اجرا می‌شوند؛ بنابراین تست عادی به اینترنت یا API پولی وابسته نیست.

Credential در User Scope و با Data Protection ذخیره می‌شود. API Key در SQLite برنامه، `appsettings`، Repository یا argument خط فرمان ذخیره نمی‌شود. متغیر `MIMO_API_KEY` فقط برای Process جاری خوانده می‌شود و به Credential Store کپی نمی‌شود.

Live Test فقط وقتی اجرا می‌شود که هم `MIMO_API_KEY` موجود باشد و هم `AGENTPULSE_RUN_LIVE_TESTS=1` به‌صورت صریح تنظیم شده باشد.

### Consequences

- رفتار Runtime از اولین نسخه Streaming با Provider واقعی هم‌راستا است.
- تست‌های معمولی deterministic، آفلاین و بدون هزینه باقی می‌مانند.
- مسئولیت‌های Application مستقل از Xiaomi و Console حفظ می‌شوند.
- Secret handling در User Scope متمرکز است و دیتابیس مکالمات حاوی API Key نیست.
- فازهای بعدی Provider واقعی یا Vertical Flow را دوباره پیاده‌سازی نمی‌کنند؛ آن‌ها روی ادامه Session، recovery، compatibility، packaging و release تمرکز می‌کنند.
- Retry خودکار Streaming، Multi-provider selection، Tool Calling و Agent Loop همچنان خارج از محدوده‌اند.

### Alternatives Considered

- نگه‌داشتن Fake Provider در Runtime تا فاز بعدی: رد شد، چون مسیر واقعی Authentication، SSE، Timeout و Failure را اثبات نمی‌کرد.
- وابسته‌کردن تست‌ها به API واقعی Xiaomi: رد شد، چون تست‌ها را پرهزینه، ناپایدار و نیازمند Secret می‌کرد.
- ذخیره API Key در SQLite یا `appsettings`: رد شد، چون Secret را با داده پروژه یا فایل Configuration مخلوط می‌کرد.
- اجرای Live Test صرفاً با وجود `MIMO_API_KEY`: رد شد، چون یک `dotnet test` عادی می‌توانست ناخواسته درخواست پولی ارسال کند.

### Status

Accepted

---

## ADR-002 — Generalize the Xiaomi transport into a single OpenAI-compatible client

### Context

فاز ۶ Transport واقعی Xiaomi MiMo را با `HttpClient`، Chat Completions، SSE افزایشی، Timeout، Cancellation و Credential امن تکمیل کرد. Protocol استفاده‌شده OpenAI-compatible بود، اما نام‌گذاری و بعضی تنظیمات Runtime به Xiaomi وابسته مانده بود. عمومی‌کردن `BaseUrl` بدون تغییر مدل امنیت Credential می‌توانست Credential ذخیره‌شده Xiaomi را پس از تغییر Configuration به Host دیگری ارسال کند. دنبال‌کردن Redirect نیز می‌توانست Header احراز هویت را خارج از Endpoint مورد اعتماد ببرد. همچنین خطاهای HTTP، Protocol، Timeout و Cancellation Contract عمومی کافی برای تشخیص وقوع قبل یا بعد از اولین Delta نداشتند.

### Decision

Runtime فقط یک Implementation فعال از `IChatModelClient` دارد: `OpenAiCompatibleChatModelClient`.

Xiaomi MiMo از طریق مقادیر پیش‌فرض `OpenAiCompatibleModelOptions` باقی می‌ماند و Provider Registry، Model Catalog یا انتخاب Provider با CLI flag ایجاد نمی‌شود. Options عمومی Base URL، مسیر نسبی Chat Completions، Model، دو حالت `Bearer` و `ApiKeyHeader`، نام Environment Variable، token limit، Thinking extension و Timeoutها را Bind می‌کند. API Key Property در Options وجود ندارد.

Transport از یک `OpenAiCompatibleSseParser` استفاده می‌کند. Adapterهای Xiaomi فقط برای سازگاری Source و Testهای فاز ۶ نگه داشته می‌شوند و Parser یا Client موازی در Composition Root ثبت نمی‌کنند.

Credential ذخیره‌شده به Scope زیر وابسته است:

```text
normalized scheme + normalized host + effective port + authentication mode + API-key header name when applicable
```

Model و Path در Scope نیستند. نام فایل Credential از SHA-256 همین Scope غیرمحرمانه ساخته می‌شود و خود Secret همچنان با Data Protection محافظت می‌شود. Credential قدیمی بدون Scope فقط برای Endpoint رسمی Xiaomi قابل خواندن است و پس از پذیرش موفق به فرمت Scoped منتقل می‌شود.

`HttpClientHandler.AllowAutoRedirect` برابر `false` است. مسیر Chat Completions باید Relative و بدون Origin، Query، Fragment یا Traversal باشد. Remote HTTP رد می‌شود و HTTP فقط برای Loopback مجاز است.

خطاها با Taxonomy عمومی `Authentication`, `PermissionDenied`, `RateLimited`, `InvalidRequest`, `Unavailable`, `Timeout`, `Protocol`, `InvalidResponse`, `Cancelled`, و `Unknown` نمایش داده می‌شوند. هر Failure مرحله `BeforeFirstToken` یا `AfterFirstToken` دارد که هنگام همان Stream و بر اساس دریافت اولین `TextDelta` ثبت می‌شود.

### Consequences

- Xiaomi MiMo بدون Command یا Registry جدید همچنان Default Provider است.
- Endpointهای OpenAI-compatible با Configuration استاندارد .NET قابل استفاده‌اند.
- Application contracts مستقل از Xiaomi باقی می‌مانند.
- Credential یک Origin یا Authentication profile به دیگری نشت نمی‌کند.
- تغییر Model یا Base path در همان Origin باعث ایجاد Credential غیرضروری جدید نمی‌شود.
- Redirect پاسخ Provider به‌جای Follow شدن به خطای Sanitized تبدیل می‌شود.
- Error body محدود خوانده می‌شود و Request body، Prompt، History، API Key و تمام Headerها وارد Exception نمی‌شوند.
- Orchestrator سیاست حفظ متن جزئی فاز ۶ را ادامه می‌دهد و Failure stage را از Run جاری دریافت می‌کند، نه از محتوای ذخیره‌شده.
- تست‌های عادی برای هر دو Profile از HTTP Test Server محلی استفاده می‌کنند و به API واقعی وابسته نیستند.

### Security considerations

- Secret از JSON، Command-line argument و SQLite خوانده یا ذخیره نمی‌شود.
- Environment Variable از Credential Store اولویت بالاتری دارد و هرگز در Store کپی نمی‌شود.
- Header name در حالت `ApiKeyHeader` اعتبارسنجی می‌شود و Headerهای حساس یا transport-controlled رد می‌شوند.
- Base URL و Path نمی‌توانند Origin نهایی را تغییر دهند.
- Redirectهای `301`, `302`, `303`, `307`, و `308` دنبال نمی‌شوند.
- Location و URLهای Error بدون Query، Fragment و UserInfo نگه داشته می‌شوند.
- Legacy Credential برای Host سفارشی قابل مشاهده یا Migration نیست.
- Scope، file name، log و exception شامل API Key، hash مشتق از API Key یا metadata آن نیستند.

### Alternatives considered

- حفظ `XiaomiChatModelClient` به‌عنوان Client اصلی و ساخت Client دوم عمومی: رد شد، چون Protocol و Parser را Duplicate و Composition Root را چندمسیره می‌کرد.
- Provider Registry و Model Catalog: رد شد، چون برای یک Transport قابل تنظیم ضروری نیست و خارج از Scope فاز ۷ است.
- استفاده از OpenAI SDK یا Xiaomi SDK: رد شد، چون کنترل دقیق SSE، Redirect، Timeout، Error sanitization و وابستگی‌ها را کاهش می‌داد.
- Scope بر اساس Model یا Base path: رد شد، چون یک Credential معمولاً چند Model و Path همان Origin را پوشش می‌دهد.
- استفاده از Credential قدیمی برای هر Host پس از تغییر Base URL: رد شد، چون خطر نشت Secret دارد.
- دنبال‌کردن Redirect و حذف Header در Redirect: رد شد، چون رفتار Handlerها و redirect chain پیچیده است و Fail-closed امن‌تر است.

### Status

Accepted
