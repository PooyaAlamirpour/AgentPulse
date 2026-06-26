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

- وضعیت: پیشنهاد
- فاز تصمیم‌گیری: ۴
- مسئله: نسخه اولیه باید جلوی دو Run هم‌زمان روی یک Session را بگیرد و پس از Crash قابل بازیابی باشد.
- گزینه‌های قابل بررسی:
  - قفل Transactional/row-based در SQLite
  - lease با owner و expiration
  - قفل درون‌پردازه‌ای همراه guard پایدار دیتابیس
- هیچ گزینه‌ای در فاز صفر انتخاب نشده است.

## P-005 — سیاست Crash Recovery

- وضعیت: پیشنهاد
- فاز تصمیم‌گیری: ۴؛ اثبات در ۹
- مسئله: Session یا Assistant Message ممکن است پس از kill ناگهانی در وضعیت Running/Streaming بماند.
- پیشنهاد: startup recovery وضعیت‌های رهاشده را تشخیص دهد و با سیاست صریح به Cancelled یا Failed تبدیل کند، بدون حذف متن ناقص.
- نیازمند تصمیم: معیار تشخیص stale، وضعیت نهایی و ثبت علت recovery.

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

- وضعیت: پیشنهاد
- فاز تصمیم‌گیری: ۳
- مسئله: پوشه Non-Git باید در اجراهای بعدی Project پایدار داشته باشد.
- گزینه‌های قابل بررسی: hash مسیر canonical، mapping دیتابیس یا شناسه ذخیره‌شده محلی.
- نیازمند تصمیم: case sensitivity، symlink، path relocation و cross-platform behavior.

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

