# قرارداد رفتاری نسخه Node برای نسخه اولیه .NET

## 1. هدف و مرز سند

این سند رفتارهای قابل مشاهده مسیر `mimo run` و قراردادهای داده‌ای مرتبط را از Snapshot موجود در آرشیو `MiMo-Code(1).7z` ثبت می‌کند تا بازنویسی .NET بر اساس حدس انجام نشود.

این سند کد Production تولید نمی‌کند و تصمیم معماری جدید را قطعی اعلام نمی‌کند. پیشنهادهای معماری تأییدنشده فقط در `docs/architecture-decisions.md` ثبت شده‌اند.

نسخه اولیه .NET فقط این جریان را هدف می‌گیرد:

```text
CLI
→ Project Context
→ Session و Message History
→ Model Request
→ یک Provider سازگار با OpenAI
→ Streaming متن
→ Persistence
→ Console
```

---

## 2. سطح قطعیت

در این سند از چهار برچسب استفاده می‌شود:

- **قطعی از مسیر اجرایی سورس**: مستقیماً از کدی که در مسیر `mimo run` اجرا می‌شود قابل اثبات است.
- **استنباطی / نیازمند منبع کامل یا آزمون Black-box**: از فایل‌های موجود نتیجه قطعی قابل استخراج نیست یا نتیجه به Runtime و سیستم‌عامل وابسته است.
- **خارج از Scope نسخه اولیه .NET**: در Node وجود دارد، اما در برنامه مصوب فازهای ۰ تا ۹ پیاده‌سازی نمی‌شود.

---

## 3. موجودی منابع

### 3.1 فایل‌های مرجع اصلی فاز صفر

فایل‌های زیر در آرشیو موجود و قابل خواندن‌اند:

```text
packages/opencode/src/index.ts
packages/opencode/src/cli/cmd/run.ts
packages/opencode/src/cli/cmd/run-completion.ts
packages/opencode/src/cli/bootstrap.ts
packages/opencode/src/project/instance.ts
packages/opencode/src/session/session.ts
packages/opencode/src/session/message-v2.ts
packages/opencode/src/session/prompt.ts
packages/opencode/src/session/processor.ts
packages/opencode/src/session/llm.ts
packages/opencode/src/provider/provider.ts
```

### 3.2 بررسی صریح مسیر Storage

در بررسی مستقیم فهرست `MiMo-Code(1).7z`، برخلاف ادعای غیبت، مسیر و فایل‌های زیر **واقعاً موجود و قابل استخراج‌اند**:

```text
packages/opencode/src/storage/
packages/opencode/src/storage/db.ts
packages/opencode/src/storage/db.bun.ts
packages/opencode/src/storage/schema.ts
```

فایل‌های مرتبط زیر نیز موجودند:

```text
packages/opencode/src/storage/schema.sql.ts
packages/opencode/src/project/project.sql.ts
packages/opencode/src/session/schema.ts
packages/opencode/src/session/session.sql.ts
```

بنابراین این Snapshot فاقد فایل‌های فوق نیست. بااین‌حال، تمام ادعاهای وابسته به این فایل‌های تکمیلی در این سند محافظه‌کارانه با برچسب «استنباطی / نیازمند منبع کامل یا آزمون Black-box» از رفتار قابل مشاهده CLI جدا شده‌اند.

### 3.3 فایل غایب

در میان فایل‌های اصلی فاز صفر و فایل‌های Storage نام‌برده‌شده، فایل غایبی در آرشیو فعلی پیدا نشد.

اگر Snapshot مرجع دیگری مدنظر باشد، نتیجه این بخش باید با همان Snapshot دوباره تولید شود و نباید به این آرشیو تعمیم داده شود.

---

## 4. Command و Optionها

### 4.1 Syntax اصلی

**قطعی از مسیر اجرایی سورس**

```bash
mimo run [message..]
```

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:202-212
```

### 4.2 Optionهای موجود در Node

**قطعی از مسیر اجرایی سورس**

Node در `run.ts` Optionهای متعددی مانند `--continue`، `--session`، `--model`، `--format`، `--file`، `--attach`، `--dir` و گزینه‌های Agent/Permission ثبت می‌کند.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:213-290
```

وجود یک Option در Node به معنی حضور آن در Scope نسخه اولیه .NET نیست.

### 4.3 Optionهای نسخه اولیه .NET مطابق برنامه مصوب

در برنامه مصوب فازهای ۰ تا ۹ فقط این ورودی‌ها برای جریان نهایی فاز ۸ در Scope هستند:

```text
mimo run "prompt"
mimo run --dir <path> "prompt"
mimo run --model <model> "prompt"
mimo run --session <id> "prompt"
```

Prompt از `stdin` نیز در Scope است.

موارد زیر صریحاً از Scope نسخه اولیه حذف شده‌اند:

```text
--continue
--format json
JSON Event Format
```

`--continue` و خروجی JSON ممکن است به‌عنوان رفتار فعلی Node ذکر شوند، اما هیچ کلاس، قرارداد، تست پذیرش یا نگاشت فاز ۱ تا ۹ برای پیاده‌سازی آن‌ها تعریف نمی‌شود.

---

## 5. ساخت Prompt از Argument و stdin

### 5.1 Prompt از Argument

**قطعی از مسیر اجرایی سورس**

- positionalها و آرگومان‌های بعد از `--` به ترتیب دریافت می‌شوند.
- آرگومان‌ها با فاصله به هم متصل می‌شوند.
- آرگومان دارای فاصله دوباره داخل کوتیشن قرار می‌گیرد و کوتیشن داخلی escape می‌شود.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:292-295
```

### 5.2 Prompt از stdin

**قطعی از مسیر اجرایی سورس**

اگر `stdin` یک TTY نباشد، کل stdin خوانده و بعد از یک newline به متن ساخته‌شده از Argument افزوده می‌شود.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:331
```

### 5.3 ورودی خالی

**قطعی از مسیر اجرایی سورس**

اگر متن نهایی بعد از `trim()` خالی باشد و `--command` نیز داده نشده باشد:

```text
You must provide a message or a command
```

چاپ می‌شود و `process.exit(1)` فراخوانی می‌شود.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:333-336
```

نرمال‌سازی احتمالی newline ابتدایی stdin در .NET یک تصمیم معماری تأییدنشده است و فقط در `architecture-decisions.md` ثبت شده است.

---

## 6. Directory و Project Context

### 6.1 مسیر پیش‌فرض

**قطعی از مسیر اجرایی سورس**

در اجرای Local، اگر `--dir` داده نشود، `process.cwd()` به `bootstrap` داده می‌شود.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:685-691
```

### 6.2 `--dir`

**قطعی از مسیر اجرایی سورس**

در حالت Local، `process.chdir(args.dir)` اجرا می‌شود. در صورت شکست، پیام خطا چاپ و Process با کد `1` بسته می‌شود.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:297-307
```

### 6.3 Git root، worktree و شناسه پایدار Project

**استنباطی / نیازمند منبع کامل یا آزمون Black-box**

فایل‌های Project و Instance شواهد لازم برای تشخیص Project Context را دارند، اما قرارداد نهایی نسخه .NET، الگوریتم شناسه پایدار، رفتار symlink و تفاوت worktreeها باید در فاز ۳ با تست‌های مشخص تثبیت شود. این سند الگوریتم جدیدی را قطعی اعلام نمی‌کند.

---

## 7. Session

### 7.1 ساخت Session جدید

**قطعی از مسیر اجرایی سورس**

اگر Session پایه انتخاب نشده باشد، CLI از SDK برای ایجاد Session استفاده می‌کند.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:367-379
```

### 7.2 ادامه Session با شناسه

**قطعی از مسیر اجرایی سورس**

`--session <id>` شناسه Session پایه را به مسیر اجرا می‌دهد. اگر Session قابل Resolve نباشد، CLI پیام `Session not found` چاپ کرده و با کد `1` خارج می‌شود.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:367-379
packages/opencode/src/cli/cmd/run.ts:625-629
```

### 7.3 `--continue`

**خارج از Scope نسخه اولیه .NET**

Node این Option را دارد و جدیدترین Session ریشه‌ای را از خروجی `session.list()` انتخاب می‌کند، اما این قابلیت در برنامه مصوب فازهای ۰ تا ۹ نسخه اولیه .NET وجود ندارد.

منبع رفتار Node:

```text
packages/opencode/src/cli/cmd/run.ts:217-221
packages/opencode/src/cli/cmd/run.ts:367-375
```

### 7.4 جلوگیری از Run هم‌زمان و Crash Recovery

**استنباطی / نیازمند منبع کامل یا آزمون Black-box**

وجود `BusyError`، `SessionRunState` و `SessionStatus` نشان می‌دهد Node مفهوم Run فعال و وضعیت Session را دارد؛ اما برابری دقیق قفل درون‌پردازه‌ای، قفل بین Processها و Recovery پس از Crash فقط از مسیر CLI به‌تنهایی ثابت نمی‌شود.

نسخه .NET باید این موارد را در فاز ۴ و سپس فازهای ۸ و ۹ با تست تثبیت کند. نوع مکانیزم قفل و Recovery در این سند تصمیم قطعی نیست.

---

## 8. Message و MessagePart

### 8.1 مدل چندبخشی

**قطعی از مسیر اجرایی سورس**

`message-v2.ts` Message و Partهای مختلف را مدل می‌کند و CLI برای Prompt یک Part متنی ارسال می‌کند.

منبع:

```text
packages/opencode/src/session/message-v2.ts
packages/opencode/src/cli/cmd/run.ts:661-668
```

نسخه اولیه .NET فقط `TextMessagePart` را پیاده‌سازی می‌کند. Tool Part، Reasoning Part و File Part خارج از Scope‌اند، ولی مدل Domain و Persistence نباید افزودن Partهای آینده را ناممکن کند.

### 8.2 ترتیب Message و Part

**استنباطی / نیازمند منبع کامل یا آزمون Black-box**

فایل‌های موجود نشان می‌دهند شناسه Message و Part با سازوکار ascending تولید می‌شود و Schema برای زمان/شناسه و message/part Index تعریف می‌کند؛ اما ترتیب نهایی همه Queryهای runtime فقط با این فایل‌ها قطعی نمی‌شود.

منبع:

```text
packages/opencode/src/session/schema.ts:18-34
packages/opencode/src/session/session.sql.ts:50-83
```

این شواهد قرارداد فعلی Node را توصیف می‌کنند. انتخاب `Sequence` عددی در .NET الزام برنامه مصوب است، نه ترجمه مستقیم Schema Node.

---

## 9. Persistence

### 9.1 نوع و محل پایگاه داده

**استنباطی / نیازمند منبع کامل یا آزمون Black-box**

implementation موجود برای SQLite طراحی شده است؛ `db.ts` مسیر پیش‌فرض را بر اساس Channel در `Global.Path.data` می‌سازد و امکان override با Flag را نشان می‌دهد. فعال‌بودن دقیق این مسیر در همه buildها و runtimeها باید با منبع build کامل یا آزمون اجرایی تأیید شود.

منبع:

```text
packages/opencode/src/storage/db.ts:30-43
packages/opencode/src/storage/db.bun.ts:1-8
```

### 9.2 تنظیمات SQLite و Migration

**استنباطی / نیازمند منبع کامل یا آزمون Black-box**

`db.ts` مسیر تنظیم WAL، foreign keys، چند PRAGMA و اجرای Migration را تعریف می‌کند؛ اجرای قطعی همین adapter و همین تنظیمات در artifact نهایی باید با build/runtime کامل تأیید شود.

منبع:

```text
packages/opencode/src/storage/db.ts:84-114
```

### 9.3 شکل کلی داده

**استنباطی / نیازمند منبع کامل یا آزمون Black-box**

Schemaهای موجود جدول‌های Project، Session، Message و Part، payloadهای JSON و چند Foreign Key/cascade را تعریف می‌کنند؛ اما Schema مؤثر artifact نهایی و migration history کامل باید جداگانه تأیید شود.

منبع:

```text
packages/opencode/src/storage/schema.ts:1-7
packages/opencode/src/project/project.sql.ts:5-16
packages/opencode/src/session/session.sql.ts:14-84
```

### 9.4 مرز Transaction و بازیابی پاسخ ناقص

**استنباطی / نیازمند منبع کامل یا آزمون Black-box**

`db.ts` abstraction برای Transaction دارد و Session updateها از مسیر Sync Event عبور می‌کنند؛ اما اینکه هر مرحله از Prompt دقیقاً در چه Transactionی Commit می‌شود و پس از kill ناگهانی چه داده‌ای قطعاً باقی می‌ماند، باید با Trace کامل مسیر Event/Projector و آزمون Black-box تثبیت شود.

در نتیجه هیچ ادعای قطعی درباره cadence ذخیره متن ناقص، atomicity کل Run یا Recovery پس از Process kill در این سند مطرح نمی‌شود.

---

## 10. Conversation History و Model Request

### 10.1 History

**قطعی از مسیر اجرایی سورس در سطح مفهومی**

Session Service امکان خواندن Messageها و Partها را دارد و Prompt/LLM مسیر تبدیل Conversation به ورودی مدل را طی می‌کنند.

منبع:

```text
packages/opencode/src/session/session.ts
packages/opencode/src/session/prompt.ts
packages/opencode/src/session/llm.ts
```

### 10.2 ترتیب دقیق نسخه اولیه .NET

مطابق برنامه مصوب، فاز ۵ باید با تست قطعی ترتیب زیر را بسازد:

```text
system
→ history
→ current user
```

رفتار Messageهای `Failed`، `Cancelled` یا ناقص باید در همان فاز مستند و تست شود. این سند سیاست جدیدی را از پیش قطعی نمی‌کند.

---

## 11. Streaming و Console

### 11.1 Event subscription

**قطعی از مسیر اجرایی سورس**

CLI به Event stream داخلی subscribe می‌کند و تا رسیدن Session به وضعیت idle یا پایان Stream، Eventها را مصرف می‌کند.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:425-440
packages/opencode/src/cli/cmd/run-completion.ts:22-76
```

### 11.2 نمایش Text در حالت پیش‌فرض Node

**قطعی از مسیر اجرایی سورس**

در حالت `default`، متن فقط وقتی چاپ می‌شود که `TextPart` دارای `time.end` باشد. بنابراین کد فعلی `mimo run` الزاماً هر Token/Delta را بلافاصله چاپ نمی‌کند.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:458-505
```

نمایش delta-by-delta در نسخه .NET یک الزام برنامه مصوب فاز ۶ و ۸ است و یک تفاوت آگاهانه با این مسیر Node محسوب می‌شود؛ جزئیات طراحی آن در سند Baseline قطعی نشده است.

### 11.3 `--format json` و JSON Event Format

**خارج از Scope نسخه اولیه .NET**

Node قابلیت چاپ JSON line برای Eventهای انتخاب‌شده را دارد، اما `--format json` و کل JSON Event Format از نسخه اولیه .NET حذف شده‌اند و در فازهای ۱ تا ۹ نگاشت یا تست پذیرش ندارند.

منبع رفتار Node:

```text
packages/opencode/src/cli/cmd/run.ts:417-423
```

### 11.4 stdout و stderr

**قطعی از مسیر اجرایی سورس**

- متن پاسخ عادی روی `stdout` نوشته می‌شود.
- خطاهای UI و Help/Fatal مسیر `stderr` دارند.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:494-505
packages/opencode/src/index.ts:61-69
packages/opencode/src/index.ts:248-255
```

رفتار کامل همه شاخه‌ها باید در فاز ۹ با Process Integration Test تثبیت شود.

---

## 12. Cancellation و Ctrl+C

### 12.1 Cancellation داخلی

**قطعی از مسیر اجرایی سورس در سطح سرویس**

Prompt، Processor و Provider از signalهای abort/cancellation استفاده می‌کنند و اجرای Tool/Stream می‌تواند abort شود.

منبع:

```text
packages/opencode/src/session/prompt.ts
packages/opencode/src/session/processor.ts
packages/opencode/src/session/llm.ts
```

### 12.2 رفتار دقیق Ctrl+C در CLI

**استنباطی / نیازمند آزمون Black-box**

از فایل‌های بررسی‌شده نمی‌توان Exit Code دقیق، ترتیب shutdown و میزان داده Persistشده در لحظه `Ctrl+C` را برای همه سیستم‌عامل‌ها قطعی اعلام کرد.

این رفتار باید در فاز ۱ سیم‌کشی شود، در فاز ۶ و ۸ روی Run واقعی اعمال شود و در فاز ۹ با Process Integration Test تثبیت شود.

---

## 13. خطای Provider و Exit Code

### 13.1 خطای Session/Provider در Event loop

**قطعی از مسیر اجرایی سورس**

رویداد `session.error` برای Session جاری به متن قابل چاپ تبدیل و از مسیر UI error نمایش داده می‌شود.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:522-532
```

### 13.2 Exit Code دقیق همه خطاها

**استنباطی / نیازمند آزمون Black-box**

برخی خطاهای usage و directory مستقیماً `process.exit(1)` دارند و خطاهای Fatal در `index.ts` نیز `process.exitCode = 1` تنظیم می‌کنند. بااین‌حال، اینکه هر خطای Provider که فقط به‌صورت Event می‌رسد حتماً Process را با کد غیرصفر می‌بندد، از مسیر موجود قطعی نیست.

منبع:

```text
packages/opencode/src/cli/cmd/run.ts:297-336
packages/opencode/src/cli/cmd/run.ts:522-532
packages/opencode/src/index.ts:248-261
```

جدول نهایی Exit Codeها باید در فاز ۹ تثبیت شود و تا آن زمان پیشنهاد معماری است، نه قرارداد قطعی Node.

---

## 14. جریان مرحله‌ای قابل اثبات Node

**قطعی از مسیر اجرایی سورس در سطح CLI**

```text
1. Parse command و optionها
2. ساخت message از Argumentها
3. اعمال --dir در حالت Local
4. افزودن stdin در حالت pipe
5. رد ورودی خالی
6. bootstrap کردن Instance محلی یا اتصال به Server
7. ساخت یا انتخاب Session
8. subscribe به Event stream
9. ارسال prompt از طریق SDK
10. مصرف Eventها تا idle شدن Session
11. چاپ TextPart کامل‌شده یا خطا
12. dispose کردن Instance در bootstrap
```

این جریان وجود SDK و Server داخلی Node را توصیف می‌کند. نسخه اولیه .NET مطابق برنامه مصوب، SDK و HTTP Server داخلی را حذف می‌کند و CLI را مستقیماً به Application Use Case متصل می‌کند.

---

## 15. Scope نهایی نسخه اولیه .NET

### داخل Scope

```text
CLI command: mimo run
Prompt از Argument و stdin
--dir
--model
--session
Cancellation
Project Context پایه
Project، Session، Message و MessagePart
TextMessagePart
SQLite و EF Core
Conversation History
Model Request مستقل از Provider
Fake Provider برای تست Streaming
یک Provider واقعی OpenAI-compatible
SSE parsing
Streaming متن روی Console
ذخیره پاسخ کامل یا ناقص
Recovery پس از خطا، Cancellation یا Crash
جلوگیری از دو Run هم‌زمان روی یک Session
stdout/stderr و Exit Codeهای مستند
```

### خارج Scope

```text
--continue
--format json
JSON Event Format
TUI
Desktop و Web UI
Server HTTP داخلی
SDK داخلی
Attach
Fork
Share
File Attachment و ورودی چندرسانه‌ای
Tool Calling
Permission
Agent و Subagent
Actor، Team، Task و Workflow
MCP
LSP
Plugin
Import و Export
Snapshot، Revert و Checkpoint
Compaction و Summary
Memory و History Search
OAuth
Provider Registry کامل
Model Catalog
Metrics، Telemetry و Control Plane
```

---

## 16. موارد باز برای فاز ۹

موارد زیر تا اجرای Integration Test واقعی نباید «رفتار قطعی Node» یا «Parity کامل» معرفی شوند:

- Exit Code دقیق `Ctrl+C` روی Windows، Linux و macOS
- Exit Code خطای Provider که فقط از Event stream گزارش می‌شود
- مقدار پاسخ ناقص باقی‌مانده پس از kill ناگهانی Process
- رفتار دقیق stdin فقط با whitespace و تفاوت line endingها
- رفتار symlink و مسیرهای دارای Space در `--dir`
- زمان دقیق آزادشدن Run lock در همه مسیرهای خطا
- جداسازی stdout/stderr در همه شاخه‌های Interactive و Non-Interactive

