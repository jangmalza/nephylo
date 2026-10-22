export const guideTranslations = [
  {
    "id": "codex",
    "name": "Codex",
    "method": "Automatic",
    "summary": "Check subscription limits using the Codex sign-in in Windows or your selected WSL distribution. This does not show remaining messages for regular ChatGPT conversations.",
    "steps": [
      "Install the Codex app or native CLI in Windows, or the Codex CLI in your chosen WSL distribution, then sign in.",
      "In the widget, open + → Codex → “Connection environment”, choose Windows or an automatically discovered WSL distribution, and select “Check connection”.",
      "If sign-in is required, select “Sign in with browser”. Closing service management keeps authentication waiting while the widget is running. Reopen it to check progress or cancel. Usage appears automatically once sign-in completes."
    ],
    "troubleshooting": [
      "If the Windows executable cannot be found, select your installed codex.exe in advanced settings. Shortcuts and codex.cmd are not supported.",
      "WSL discovery uses the selected distribution’s default Linux user. Install Codex and sign in as that user, then select “Search again”. Sign-ins are not copied between distributions.",
      "If WSL is unavailable, open that distribution in Windows Terminal once and search again. Your existing environment stays selected until the connection check succeeds.",
      "Sign in again if your session expired. If WSL browser sign-in does not complete, run codex login in that distribution, then check the connection in the widget."
    ],
    "docsLabel": "Codex installation guide"
  },
  {
    "id": "eleven",
    "name": "ElevenLabs",
    "method": "Automatic",
    "summary": "Check subscription credits for the connected account or workspace. Both web and API usage are reflected. Separate PAYG prepaid balances are not shown.",
    "steps": [
      "Create a key under Developers → API Keys in ElevenLabs.",
      "Enable User → Read in the key permissions.",
      "In the widget, open + → ElevenLabs, paste the key and select “Check and save connection”."
    ],
    "troubleshooting": [
      "For permission errors, enable User → Read for the key you are using and try again.",
      "Check key expiry and IP restrictions. If you replaced the key, save the new key in the widget too.",
      "A limit set on an individual API key can differ from the remaining subscription credits for the account."
    ],
    "docsLabel": "API keys and permissions"
  },
  {
    "id": "openai",
    "name": "OpenAI API",
    "method": "Automatic cost tracking",
    "summary": "View this month’s organization-wide API costs in USD, using UTC month boundaries. Set an optional monthly budget to show an estimated amount remaining. Prepaid balances and ChatGPT subscription limits are not checked.",
    "steps": [
      "Create an Admin API key from an organization owner account.",
      "In the widget, open + → OpenAI API, enter the key and select “Check and save connection”.",
      "Optionally enter a monthly budget in advanced settings. This does not change any actual billing limit."
    ],
    "troubleshooting": [
      "A regular model API key cannot read these costs. Make sure you are using an organization owner’s Admin API key.",
      "Costs may take time to appear. When comparing with the official usage page, check the organization and UTC month."
    ],
    "docsLabel": "Admin API guide"
  },
  {
    "id": "claude",
    "name": "Claude",
    "method": "Claude Code automatic queries",
    "summary": "Query 5-hour and weekly remaining quotas without a new conversation using official Claude Code 2.1.304 or later and a Pro/Max sign-in. Legacy receipts and manual records are preserved.",
    "steps": [
      "Install official Claude Code 2.1.304 or later in Windows or your chosen WSL distribution and sign in with a Pro or Max account.",
      "In the widget, open + → Claude → “Connection environment”, choose Windows or an automatically discovered WSL distribution, and select “Connect Claude Code”.",
      "Select “Check remaining usage”, then save. Widget refresh and automatic polling update the values without sending a new Claude conversation."
    ],
    "troubleshooting": [
      "If direct queries are unavailable, check your CLI version and Pro/Max sign-in. Free/API-key or web-only users can use manual records. Legacy “Waiting for first reading” and “Received · Limits unavailable” states are different from sign-in failures.",
      "Choose a custom Windows settings folder in Advanced. Leave it blank to use CLAUDE_CONFIG_DIR or the default folder. To keep an existing status line, forward the same JSON input to the copied receiver command, enable “Receive from existing status line”, then inspect the received values. Your script is preserved.",
      "WSL uses ~/.claude/settings.json for the default Linux user and requires Windows interoperability. Custom WSL settings folders are not supported. If a legacy lock remains, close the previous widget and connection tasks before checking its state.",
      "The next query can reflect usage from other devices or the web. Refresh stale values and check the last query or receipt time alongside the reading."
    ],
    "docsLabel": "Legacy Claude Code status-line guide"
  },
  {
    "id": "gemini",
    "name": "Gemini API",
    "method": "Manual",
    "summary": "Manually record one limit checked on the official page. Limits vary by project, model and time window; this is not a combined remaining percentage across all usage.",
    "steps": [
      "In the widget, open + → Gemini API and follow the link to official usage or model limits.",
      "Use the model and limit type as the metric name, then enter the remaining percentage. A reset time is optional.",
      "Select “Save as checked now”. No API key is required."
    ],
    "troubleshooting": [
      "Automatic refresh does not change manual entries. Recheck the official page and save a new value yourself.",
      "After a recorded reset time passes, the value is marked expired. The widget does not assume 100% remaining; check and record the latest value."
    ],
    "docsLabel": "Gemini API limits"
  },
  {
    "id": "cursor",
    "name": "Cursor",
    "method": "Manual",
    "summary": "Record personal subscription usage from the official Cursor settings page. This widget does not automatically retrieve usage for personal subscriptions.",
    "steps": [
      "In the widget, open + → Cursor and follow the link to the official usage page.",
      "Enter the metric name and remaining percentage you checked. A reset time is optional.",
      "Select “Save as checked now”. No API key is required."
    ],
    "troubleshooting": [
      "Rather than waiting for automatic refresh, check the official settings page and save a new value.",
      "If the record differs from current usage, check its last recorded time. Team administrator API connections are not supported."
    ],
    "docsLabel": "Open official Cursor settings"
  },
  {
    "id": "higgsfield",
    "name": "Higgsfield web",
    "method": "Automatic via CLI",
    "summary": "Check website credits after signing in with the official Higgsfield CLI. A single workspace connects automatically; if several are available, select one in the widget. These credits are separate from the developer API’s dollar balance. The current balance and check time are shown.",
    "steps": [
      "Install the official Higgsfield CLI. It is not bundled with the widget; see the official installation guide linked from the connection screen.",
      "In the widget, open + → Higgsfield web, select “Sign in to Higgsfield” and finish signing in in your browser. Closing service management keeps authentication waiting while the widget is running. Reopen it to check progress or cancel.",
      "Already signed in with the CLI? Select “Check connection”. A single workspace connects automatically. If several are available, choose one from the list and select “Connect selected workspace”. After connection, checks follow the widget’s refresh interval."
    ],
    "troubleshooting": [
      "If the CLI cannot be found, specify the installed higgsfield.exe path. For sign-in errors, authenticate again with “Sign in to Higgsfield”, then recheck the connection.",
      "If asked to select a workspace, choose one from the list and select “Connect selected workspace”. Use “Load workspaces” to refresh the list. To specify an ID manually, copy the lookup command from advanced settings, run it in PowerShell, enter the returned ID, and select “Check connection”.",
      "Only a balance is supplied, so percentages and reset times are not estimated. Set a separate low-credit threshold; the default is 20 credits. Reset alerts do not apply.",
      "Web credits and API balance can appear together as separate entries. They are not added together because they are different balances."
    ],
    "docsLabel": "Official Higgsfield CLI and MCP guide"
  },
  {
    "id": "higgsfield-api",
    "name": "Higgsfield API",
    "method": "Manual USD balance",
    "summary": "Manually record the API balance in USD from the developer console. No public balance lookup API has been confirmed, so automatic connection is not offered. You do not need to enter an API key.",
    "steps": [
      "In the widget, open + → Higgsfield API and select “Balance page ↗” to open the official developer console.",
      "Check the current balance under Billing and enter the USD amount. It is separate from web subscription credits.",
      "Select “Save checked balance”. Zero dollars is also accepted, and the check time is saved with the balance."
    ],
    "troubleshooting": [
      "Automatic refresh does not change the amount or check time. Check the latest balance in the console and save it manually.",
      "Registering an API key does not enable automatic balance lookup. Do not paste a key into this screen.",
      "Set a separate USD low-balance threshold; the default is $5. Alerts are evaluated only when a new manual balance is saved. Reset alerts do not apply, and history starts with new saved balances after recording is enabled."
    ],
    "docsLabel": "Higgsfield API and separate billing"
  }
];

export const faqTranslations = [
  {
    "question": "Are all features free?",
    "answer": "Yes. Service connections, alerts, up to 30 days of history and CSV export, remaining-usage insights, weekly summaries, themes, the dashboard and minibar are free without a Nephylo account or payment. AI service accounts, subscriptions and API charges are separate."
  },
  {
    "question": "Can I view several services together?",
    "answer": "Yes. Use + at the top of the widget to connect your services and view them together. There is no limit on supported service types, with one account per service. Unconnected services stay hidden."
  },
  {
    "question": "Do I still need to pay for Pro?",
    "answer": "No. Nephylo no longer has a paid plan or feature restrictions. All widget features work without a payment card or entitlement check."
  },
  {
    "question": "Are AI service logins also unnecessary?",
    "answer": "Only the Nephylo sign-in is unnecessary. Connected services such as Codex, Claude Code and Higgsfield may still require their own sign-in, API key or installed software. Check each connection guide."
  },
  {
    "question": "Will features lock when my old Pro period expires?",
    "answer": "The free version does not check Nephylo entitlement dates, so an old Pro expiry or sign-out will not lock features. Expiry of an AI provider subscription may still change what that provider exposes."
  },
  {
    "question": "What if the Nephylo sign-in server is unavailable?",
    "answer": "The widget does not contact Nephylo sign-in servers to unlock features. Without internet access, fresh provider readings and update downloads may be unavailable, but saved settings and history remain."
  },
  {
    "question": "Are existing accounts deleted?",
    "answer": "Switching to free access does not automatically delete existing accounts. To delete a previous Nephylo account, follow the account-deletion instructions in the privacy notice. This does not delete provider accounts or local settings and history."
  },
  {
    "question": "Do I need to sign up or sign in?",
    "answer": "No Nephylo account is required. Install the widget and connect the AI services you use. All widget features work without the previous Google or email sign-in."
  },
  {
    "question": "Can Claude Free automatically show remaining usage?",
    "answer": "Free Nephylo access is separate from Claude account capabilities. If Claude Code does not provide subscription quota data for a Claude Free or API account, automatic remaining usage is unavailable. You can use manual entries; missing quota data does not mean sign-in failed."
  },
  {
    "question": "Does losing internet access lock features?",
    "answer": "Local features such as saved history, insights and themes remain available. Fresh readings require a connection to the relevant provider. Older readings show their last checked time and are identified as previous values."
  },
  {
    "question": "Does unlimited registration include AI service fees?",
    "answer": "No. It means there is no limit on supported service types, with one account per service. It does not increase provider quotas or add multi-account support. Provider subscriptions and API charges are separate."
  },
  {
    "question": "Where are API keys stored?",
    "answer": "Do not enter AI API keys on this website. ElevenLabs and OpenAI keys entered in the widget are encrypted on your PC using Windows DPAPI for the current Windows user. Re-enter them on another PC or Windows account; remove them in service settings. AI keys and usage history are not sent to Nephylo servers."
  },
  {
    "question": "What do I need to run the widget?",
    "answer": "Windows 10 version 1903 (build 18362) or later on x64, or Windows 11 on x64 or ARM64, and .NET Framework 4.8 or later are required. The widget itself does not require a separate Python or Node.js installation. Codex, Claude Code and Higgsfield web connections require their respective application or official CLI. Follow the official installation guidance for those external tools."
  },
  {
    "question": "Do usage checks consume AI credits or change my plan?",
    "answer": "The widget reads usage. It does not send conversations to a model or change billing, plans or actual spending limits. A monthly budget entered in the widget is only used to calculate an indicative amount remaining. Connecting Claude Code registers a local status-line setting."
  },
  {
    "question": "Do unconnected services remain visible?",
    "answer": "Connected supported services appear together; unconnected services stay hidden. First launch shows connection guidance. If a query fails, the service remains visible with its error and troubleshooting guidance."
  },
  {
    "question": "Does every service update in real time?",
    "answer": "Codex, ElevenLabs, OpenAI API, Higgsfield web and supported Claude Code connections are queried at scheduled intervals. Legacy Claude receipts update after new quota data arrives. Gemini, Cursor and Higgsfield API use manual records. Check the last checked time alongside each value."
  },
  {
    "question": "Can I view Higgsfield web credits and API balance together?",
    "answer": "Yes. Web credits use the official CLI and a connected workspace for automatic readings. The API USD balance is entered manually from the developer console. Both can be displayed for free; they remain separate because they are different billing products."
  },
  {
    "question": "Are alerts available for Higgsfield balances?",
    "answer": "Set separate low-balance thresholds for web credits and API USD balance. Defaults are 20 credits and $5. Web alerts use fresh readings; API alerts are evaluated when a new manual balance is saved. The same low state is not repeatedly reported. Reset alerts are unavailable because no reset time is provided."
  },
  {
    "question": "What do reset alerts tell me?",
    "answer": "They tell you that a reset time supplied by the service has passed. They do not verify a refill or change the balance to 100%. Refresh the service or review your manual entry. Alerts work only while the widget runs and depend on Windows notification and Do Not Disturb settings."
  },
  {
    "question": "How is usage history stored?",
    "answer": "History begins with new readings after recording is enabled; past usage is not imported. The latest 24 hours retain detailed readings, and older history keeps the last observed value per time bucket for up to 30 days. Records contain values, timestamps and sources, never API keys or conversation content. Viewing and CSV export are free."
  },
  {
    "question": "Can I keep my existing settings and history?",
    "answer": "Yes. Service connections, theme and alert preferences, and saved history are retained. You do not need to sign in again to use features. History recording and alerts follow your saved preferences and can be enabled as needed."
  },
  {
    "question": "How do I install or update the widget?",
    "answer": "Download and run the single Nephylo-Setup.exe file. No ZIP extraction is needed. Follow the setup instructions, choose whether to start with Windows, and launch the widget from the finish screen. Run a new installer to update while keeping existing connections and settings. If prompted to close the running widget, quit from its tray menu and continue. To use without installation, choose the portable ZIP in the download file details."
  },
  {
    "question": "What if a service is missing after restarting?",
    "answer": "You do not need to sign in to a Nephylo account. Use + to check the service connection and error guidance. If the provider login has expired, authenticate that provider again and refresh. Existing settings and history are retained."
  }
];
