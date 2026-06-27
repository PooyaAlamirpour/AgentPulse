# پیشنهادهای معماری نیازمند تصویب

## وضعیت سند

تمام موارد این سند **پیشنهاد** هستند و هنوز ADR پذیرفته‌شده یا قرارداد قطعی نسخه .NET محسوب نمی‌شوند. فاز صفر هیچ‌کدام را پیاده‌سازی نمی‌کند.

هر پیشنهاد باید در فاز مقصد با تست، Trade-off و تصمیم صریح Accepted/Rejected نهایی شود.

---

## P-001 — نمایش Delta واقعی در Console

- وضعیت: پیشنهاد ناشی از الزام برنامه مصوب
- فاز تصمیم‌گیری: ۶؛ تأیید نهایی در ۸ و ۹
- مسئله: مسیر فعلی Node در حالت default، TextPart را پس از `time.end` چاپ می‌کند، اما برنامه .NET نمایش فوری Delta را می‌خواهد.
- پیشنهاد: `IChatModelClient` رویداد `TextDelta` بدهد و renderer همان Delta را بدون انتظار برای پایان Part چاپ کند.
- نیازمند تصمیم: رفتار newline، buffering، TTY و non-TTY، و recovery هنگام قطع وسط stream.

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

- وضعیت: پیشنهاد
- فاز تصمیم‌گیری: ۶
- مسئله: ذخیره هر Token هزینه Transaction زیاد دارد و ذخیره فقط در پایان recovery را ضعیف می‌کند.
- پیشنهاد: flush بر اساس زمان یا آستانه حجم، سپس flush اجباری روی completion/failure/cancellation.
- نیازمند تصمیم: مقادیر threshold، transaction boundary و backpressure.

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

- وضعیت: پیشنهاد معماری برنامه مصوب
- فاز تصمیم‌گیری: ۱ و ۸
- مسئله: Node CLI از SDK و Server داخلی استفاده می‌کند.
- پیشنهاد: نسخه اولیه .NET مستقیماً `RunPrompt` را از Application اجرا کند و CLI به EF Core، HttpClient یا Git process وابسته نشود.
- نیازمند تصمیم: مرز Composition Root و adapterهای renderer/configuration.

