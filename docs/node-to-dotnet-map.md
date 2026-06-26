# نگاشت رفتار Node به برنامه مصوب فازهای ۰ تا ۹

## 1. هدف

این سند هر قابلیت نسخه اولیه را فقط به فازهای مصوب ۰ تا ۹، لایه مقصد و مسئولیت پیشنهادی .NET نگاشت می‌کند. این نگاشت ترجمه فایل‌به‌فایل TypeScript نیست و هیچ فاز جدید یا فازبندی موازی تعریف نمی‌کند.

پیشنهادهای معماری تأییدنشده در `docs/architecture-decisions.md` نگهداری می‌شوند و در این سند به‌عنوان تصمیم نهایی معرفی نمی‌شوند.

---

## 2. وضعیت منابع

فایل‌های اصلی فاز صفر در آرشیو موجودند. بررسی مستقیم آرشیو همچنین وجود مسیر و فایل‌های زیر را تأیید می‌کند:

```text
packages/opencode/src/storage/
packages/opencode/src/storage/db.ts
packages/opencode/src/storage/db.bun.ts
packages/opencode/src/storage/schema.ts
```

در نتیجه، این فایل‌ها در Snapshot فعلی «غایب» نیستند. بااین‌حال، نگاشت‌های وابسته به آن‌ها به‌عنوان قرارداد داده‌ای و Persistence شناخته می‌شوند، نه رفتار Black-box قطعی CLI.

---

## 3. فازهای مصوب

### فاز ۰ — ثبت قرارداد رفتاری نسخه Node

فقط مستندات Baseline و نگاشت تولید می‌شود. هیچ کد Production، Solution، پروژه Console، Migration یا تست اجرایی جدید ساخته نمی‌شود.

### فاز ۱ — اسکلت Solution و Composition Root

- Solution و پروژه‌ها
- Referenceهای یک‌طرفه
- nullable و TreatWarningsAsErrors
- Generic Host، DI، Options و Logging
- Command پایه `mimo run`
- Prompt از Argument و stdin
- سیم‌کشی `Ctrl+C`
- بدون دیتابیس، Session و Provider

### فاز ۲ — Domain و Persistence پایه

- Project، Session، Message، MessagePart و TextMessagePart
- وضعیت‌ها، شناسه‌ها، UTC و Sequence
- EF Core، SQLite، Repository و Unit of Work
- Migration اول، Foreign Key و Indexها
- بدون Project Context و Provider

### فاز ۳ — ساخت Project Context

- مسیر ورودی و normalization
- Current Directory
- Git repository/root/worktree
- شناسه پایدار Project
- Context پایه و مدیریت نبودن Git

### فاز ۴ — چرخه Session و Message

- Get/Create Project
- ساخت Session یا ادامه با `--session`
- اعتبارسنجی Session و Project
- User/Assistant Message lifecycle
- History با ترتیب قطعی
- Session Run Lock و Recovery

### فاز ۵ — ساخت Model Request

- قراردادهای مستقل از Provider
- `IChatModelClient`
- System Context
- تبدیل History
- ترتیب `system → history → current user`
- بدون Provider واقعی، Tool و Agent

### فاز ۶ — Streaming با Provider آزمایشی

- Fake Model Client
- `IAsyncEnumerable<ModelStreamEvent>`
- TextDelta/Completed/Failed/Usage
- Console delta rendering
- partial persistence، completion، failure و cancellation

### فاز ۷ — Provider واقعی OpenAI-Compatible

- HttpClient/IHttpClientFactory
- Configuration و Secret handling
- SSE parser
- delta، finish reason و usage
- HTTP/application error normalization و timeout

### فاز ۸ — اتصال جریان عمودی نهایی

- `RunPrompt` کوچک و orchestration کامل
- CLI به Application بدون SDK/Server داخلی
- `--dir`، `--model`، `--session` و stdin
- Run lock، history، streaming، partial persistence و cleanup

### فاز ۹ — سخت‌سازی CLI و آزمون سازگاری

- مقایسه با Baseline فاز صفر
- Exit Codeها و stdout/stderr
- Interactive/Non-Interactive، stdin و Ctrl+C
- مسیر نامعتبر/Space، Session نامعتبر و Configuration مفقود
- partial response و Crash Recovery
- Logging قابل تنظیم و مستندات اجرا
- بدون قابلیت جدید

---

## 4. موارد صریحاً خارج از Scope فازهای ۰ تا ۹

موارد زیر نباید در هیچ نگاشت، کلاس یا معیار پذیرش نسخه اولیه وارد شوند:

```text
--continue
--format json
JSON Event Format
Attach
Fork
Share
File Attachment
Tool Calling
Permission
Agent/Subagent
Actor/Team/Task/Workflow
MCP
LSP
Plugin
TUI/Desktop/Web UI
Server HTTP داخلی و SDK داخلی
Snapshot/Revert/Checkpoint
Compaction/Summary
Memory/History Search
OAuth
Provider Registry کامل و Model Catalog
Metrics/Telemetry/Control Plane
```

وجود این قابلیت‌ها در Node فقط برای شناخت مسیر فعلی است و مجوز پیاده‌سازی زودهنگام آن‌ها نیست.

---

## 5. نگاشت سطح بالا

| رفتار یا قرارداد | منبع Node | فاز مصوب | مقصد .NET |
|---|---|---:|---|
| Parse فرمان `mimo run` | `cli/cmd/run.ts` | ۱ | `Mimo.Cli` / Run command |
| Prompt از Argument | `cli/cmd/run.ts` | ۱ | `PromptInputReader` یا handler کوچک CLI |
| Prompt از stdin | `cli/cmd/run.ts` | ۱ | `PromptInputReader` |
| ورودی خالی و usage error | `cli/cmd/run.ts` | ۱ و ۹ | CLI validation + process integration test |
| اتصال Ctrl+C به CancellationToken | prompt/processor/llm + CLI runtime | ۱ | Composition Root |
| Project/Session/Message/Part contracts | `message-v2.ts`، SQL schemaها | ۲ | `Mimo.Domain` |
| SQLite schema و Migration | `storage/*` و `*.sql.ts` | ۲ | `Mimo.Infrastructure.Persistence` |
| Project Context و Git/worktree | `project/*` و `instance.ts` | ۳ | `IProjectContextResolver` + `IGitClient` |
| ساخت Session | `session/session.ts` و `run.ts` | ۴ | `CreateSession` |
| ادامه با Session ID | `run.ts` و Session service | ۴ | `ContinueSession` |
| User Message lifecycle | `session/prompt.ts` و Session service | ۴ | `AppendUserMessage` |
| Assistant Message lifecycle | `session/prompt.ts` و Processor | ۴ و ۶ | Create/Complete/Fail/Cancel use cases |
| History با ترتیب قطعی | Session service/schema | ۴ | `LoadConversationHistory` |
| جلوگیری از Run هم‌زمان | RunState/Status/BusyError | ۴ | Session run lock abstraction |
| Recovery Session رهاشده | RunState/Status + Persistence | ۴ و ۹ | recovery service + integration test |
| Provider-neutral request/events | `session/llm.ts` و `message-v2.ts` | ۵ | Application contracts |
| ساخت System + History + User request | `system.ts`، `prompt.ts`، `llm.ts` | ۵ | `BuildModelRequest` |
| Streaming آزمایشی قطعی | mock/test LLM files | ۶ | Fake `IChatModelClient` |
| نمایش فوری TextDelta | تفاوت آگاهانه با `run.ts` | ۶ | `IConsoleRenderer` adapter |
| ذخیره دوره‌ای پاسخ ناقص | Processor/Part updates | ۶ | streaming persistence coordinator |
| OpenAI-compatible HTTP | provider/config/auth files | ۷ | `OpenAiCompatibleChatModelClient` |
| SSE parser | Provider/LLM transport | ۷ | parser مستقل و قابل تست |
| Orchestrator نهایی | `run.ts` + prompt/processor | ۸ | `RunPrompt` |
| CLI end-to-end واقعی | `run.ts` | ۸ | `Mimo.Cli` composition |
| Exit Code/stdout/stderr parity | `run.ts` و `index.ts` | ۹ | Process integration tests |
| Ctrl+C و Crash Recovery process-level | runtime + Persistence | ۹ | CLI integration tests |

---

## 6. Domain Mapping — فاز ۲

### Project

حداقل داده‌های نسخه اولیه:

```text
ProjectId
NormalizedRootPath
IsGitRepository
GitWorktree
CreatedAtUtc
UpdatedAtUtc
```

الگوریتم شناسه و رفتار Non-Git در فاز ۳ تکمیل می‌شود؛ فاز ۲ فقط Contract و Persistence پایه را فراهم می‌کند.

### Session

```text
SessionId
ProjectId
Status: Idle | Running
CreatedAtUtc
UpdatedAtUtc
```

`--continue` در مدل یا Use Case نسخه اولیه وجود ندارد. ادامه فقط با شناسه صریح Session انجام می‌شود.

### Message

```text
MessageId
SessionId
Role
Status: Pending | Streaming | Completed | Failed | Cancelled
Sequence
CreatedAtUtc
UpdatedAtUtc
```

Sequence مستقل از زمان برای ترتیب قطعی الزامی است.

### MessagePart

```text
MessagePart base
TextMessagePart
Part order/discriminator
```

فقط Text Part پیاده‌سازی می‌شود. کلاس‌های Tool/Reasoning/File در فاز ۲ ساخته نمی‌شوند؛ Persistence فقط باید امکان evolution آینده را مسدود نکند.

### ModelReference

Contract پایه می‌تواند در Domain یا Application بر اساس وابستگی واقعی قرار گیرد، اما نباید Provider SDK یا Configuration را وارد Domain کند. placement نهایی یک پیشنهاد معماری است و در `architecture-decisions.md` ثبت می‌شود.

---

## 7. Application Mapping

### فاز ۳

```text
BuildProjectContext
IProjectContextResolver
IGitClient
IFileSystem
IClock
```

### فاز ۴

```text
GetOrCreateProject
CreateSession
ContinueSession
AppendUserMessage
CreateAssistantMessage
LoadConversationHistory
Acquire/Release Session Run Lock
RecoverInterruptedSessions
```

مرز Transactionها باید در فاز ۴ مستند و تست شود. نوع دقیق قفل از پیش قطعی نشده است.

### فاز ۵

```text
ChatModelRequest
ChatModelMessage
ChatModelRole
ModelStreamEvent
ModelUsage
ModelFinishReason
IChatModelClient
BuildModelRequest
```

### فاز ۶

```text
StreamModelResponse
CompleteAssistantMessage
FailAssistantMessage
CancelAssistantMessage
IConsoleRenderer
FakeChatModelClient در Test project
```

### فاز ۸

```text
RunPrompt
```

`RunPrompt` فقط Orchestrator است و مسئولیت‌های Project، Session، Request building، Streaming و Persistence را به Use Caseهای کوچک واگذار می‌کند.

---

## 8. Infrastructure Mapping

### فاز ۲ — EF Core و SQLite

```text
MimoDbContext
ProjectRepository
SessionRepository
MessageRepository
UnitOfWork
Migrationها
SQLite configuration
```

منابع Node:

```text
packages/opencode/src/storage/db.ts
packages/opencode/src/storage/db.bun.ts
packages/opencode/src/storage/schema.ts
packages/opencode/src/storage/schema.sql.ts
packages/opencode/src/project/project.sql.ts
packages/opencode/src/session/session.sql.ts
```

این فایل‌ها در آرشیو فعلی موجودند. طراحی EF Core باید از برنامه مصوب پیروی کند و نباید ترجمه خط‌به‌خط Drizzle باشد.

### فاز ۳ — Git و File System

```text
GitClient
PhysicalFileSystem
SystemClock
Id generator implementation در صورت نیاز
```

### فاز ۷ — Provider و SSE

```text
OpenAiCompatibleChatModelClient
SseParser
Provider configuration
HttpClient registration
Error mapper
```

### فاز ۹ — Logging و hardening

Logging قابل تنظیم، Secret redaction و testهای process-level در این فاز نهایی می‌شوند؛ Telemetry/Control Plane خارج از Scope است.

---

## 9. CLI Mapping

### فاز ۱

CLI فقط این مسئولیت‌ها را دارد:

```text
Composition Root
DI/Configuration bootstrap
parse mimo run
read Argument/stdin
validate empty input
Ctrl+C → CancellationToken
stdout/stderr پایه
```

در این فاز دیتابیس، Session، Provider و RunPrompt کامل ساخته نمی‌شوند.

### فاز ۸

CLI handler ورودی validated را به `RunPrompt` می‌دهد و نتیجه/خطا را به renderer و exit-code mapping منتقل می‌کند. CLI نباید مستقیماً به DbContext، SQL، HttpClient یا Git process وابسته شود.

### فاز ۹

Process واقعی CLI برای Interactive/Non-Interactive، stdin، Ctrl+C، path with spaces، invalid directory/session/config، provider failure و crash recovery تست می‌شود.

هیچ handler برای `--continue` یا `--format json` در نسخه اولیه تعریف نمی‌شود.

---

## 10. نگاشت رفتار به تست

| رفتار | Test project | فاز |
|---|---|---:|
| Prompt argument parsing | `Mimo.Cli.IntegrationTests` یا تست parser | ۱ |
| stdin input | `Mimo.Cli.IntegrationTests` | ۱ و ۹ |
| empty input | `Mimo.Cli.IntegrationTests` | ۱ و ۹ |
| dependency direction | Architecture test یا build graph | ۱ |
| Domain invariants | `Mimo.Domain.Tests` | ۲ |
| SQLite CRUD/relations/migration | `Mimo.Infrastructure.Tests` | ۲ |
| deterministic message sequence | Domain/Application/Infrastructure tests | ۲ و ۴ |
| Git root/worktree/non-Git | `Mimo.Infrastructure.Tests` | ۳ |
| create/continue-by-id Session | `Mimo.Application.Tests` | ۴ |
| Session belongs to Project | `Mimo.Application.Tests` | ۴ |
| single active Run per Session | Application + SQLite integration | ۴ |
| interrupted Session recovery | Infrastructure/Application integration | ۴ و ۹ |
| request order and no duplicate prompt | `Mimo.Application.Tests` | ۵ |
| failed/cancelled history policy | `Mimo.Application.Tests` | ۵ |
| Hel + lo → Hello | Application end-to-end with Fake Provider | ۶ |
| cancellation keeps partial text | Application end-to-end | ۶ |
| stream failure keeps partial text | Application end-to-end | ۶ |
| fragmented/multiline SSE and `[DONE]` | `Mimo.Infrastructure.Tests` | ۷ |
| HTTP cancellation and secret redaction | `Mimo.Infrastructure.Tests` | ۷ |
| full RunPrompt flow | end-to-end with Fake Provider | ۸ |
| real CLI process, stdout/stderr/exit codes | `Mimo.Cli.IntegrationTests` | ۹ |
| Ctrl+C and crash recovery | `Mimo.Cli.IntegrationTests` | ۹ |

---

## 11. Transaction Map مطابق فازها

این مرزها هدف تست و طراحی فازهای مربوط‌اند و implementation قطعی فاز صفر نیستند:

### فاز ۲

- Migration و schema creation
- CRUD مستقل Project/Session/Message/Part

### فاز ۴

- Get/Create Project
- تخصیص Sequence قطعی
- ذخیره User Message قبل از Provider
- ایجاد Assistant Message و Text Part قبل از اولین Delta
- acquire/release Run lock
- recovery وضعیت Running رهاشده

### فاز ۶

- flush دوره‌ای متن ناقص
- completion/failure/cancellation transition

### فاز ۸

- orchestration ترتیب مراحل و cleanup همه مسیرها

### فاز ۹

- اثبات recovery پس از قطع واقعی Process

cadence دقیق flush، نوع Lock و atomicity هر مجموعه عملیات هنوز تصمیم معماری تأییدنشده‌اند.

---

## 12. تفاوت‌های مشاهده‌شده یا مورد انتظار

- Node `run` در حالت default، TextPart کامل‌شده را چاپ می‌کند؛ برنامه مصوب .NET نمایش فوری Delta را الزام می‌کند.
- Node از SDK و Server داخلی استفاده می‌کند؛ نسخه اولیه .NET مستقیماً Application Use Case را صدا می‌زند.
- Node Optionهای بسیار بیشتری دارد؛ نسخه اولیه فقط `--dir`، `--model` و `--session` را همراه Prompt/stdin پوشش می‌دهد.
- `--continue`، `--format json` و JSON Event Format عمداً خارج از Scope‌اند.
- Exit Code نهایی Provider error و Ctrl+C باید در فاز ۹ تعیین تکلیف شود و در فاز صفر قطعی نیست.

---

## 13. Definition of Done فاز صفر اصلاح‌شده

- فقط اسناد تولید یا اصلاح شده‌اند.
- هیچ پروژه Console، `Program.cs`، Migration، کلاس Production یا تست جدیدی در فاز صفر ایجاد نشده است.
- فازبندی موازی ۱ تا ۶ حذف شده و همه نگاشت‌ها فقط به فازهای مصوب ۰ تا ۹ اشاره می‌کنند.
- `--continue`، `--format json` و JSON Event Format از Scope نسخه اولیه حذف شده‌اند.
- تصمیم‌های معماری تأییدنشده به `architecture-decisions.md` منتقل شده‌اند.
- ادعاهای Persistence از رفتار قابل مشاهده CLI جدا و با منبع تکمیلی مشخص شده‌اند.
- موجودی آرشیو دوباره بررسی شده و فایل‌های Storage نام‌برده‌شده در Snapshot فعلی موجودند.

