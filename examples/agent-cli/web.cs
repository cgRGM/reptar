namespace ReptarWeb;

// Single-file web companion served at "/". React via ESM CDN keeps the binary
// self-contained (no build step, no node_modules for the example app); every
// call goes through plain fetch + SSE, and the same endpoints work with
// @reptar/client's typed createClient() in a userland React app.
public static class WebSpa
{
    public const string IndexHtml = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <title>Reptar Agent</title>
        <link rel="preconnect" href="https://fonts.googleapis.com">
        <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
        <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500;600&display=swap" rel="stylesheet">
        <style>
          :root {
            --bg: #09090b;
            --sidebar: #0c0d12;
            --sidebar-border: #1a1d27;
            --card: #13151f;
            --card-border: #222638;
            --card-hover: #191c2b;
            --border: #222638;
            --border-subtle: #191c28;
            --text: #f4f4f7;
            --text-muted: #8e95ab;
            --text-dim: #5c6275;
            --accent: #8b5cf6;
            --accent-hover: #7c3aed;
            --accent-subtle: rgba(139, 92, 246, 0.12);
            --accent-glow: rgba(139, 92, 246, 0.28);
            --green: #10b981;
            --green-subtle: rgba(16, 185, 129, 0.12);
            --green-border: rgba(16, 185, 129, 0.28);
            --red: #f43f5e;
            --red-subtle: rgba(244, 63, 94, 0.12);
            --red-border: rgba(244, 63, 94, 0.28);
            --amber: #f59e0b;
            --amber-subtle: rgba(245, 158, 11, 0.12);
            --cyan: #06b6d4;
            --font-sans: 'Inter', -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            --font-mono: 'JetBrains Mono', ui-monospace, "SF Mono", Menlo, Consolas, monospace;
            --radius-sm: 6px;
            --radius-md: 10px;
            --radius-lg: 14px;
            --radius-xl: 20px;
          }
          * { box-sizing: border-box; margin: 0; padding: 0; }
          html, body, #root { height: 100%; width: 100%; overflow: hidden; background: var(--bg); color: var(--text); font-family: var(--font-sans); }
          button, input, textarea { font-family: inherit; }
          a { color: var(--accent); text-decoration: none; }
          a:hover { text-decoration: underline; }

          /* App shell */
          .app-layout { display: flex; width: 100vw; height: 100vh; overflow: hidden; }

          /* Sidebar */
          aside.sidebar {
            width: 280px; min-width: 280px; background: var(--sidebar);
            border-right: 1px solid var(--sidebar-border); display: flex; flex-direction: column;
            transition: transform .2s ease, width .2s ease; z-index: 20;
          }
          aside.sidebar.collapsed { width: 0; min-width: 0; transform: translateX(-100%); overflow: hidden; }
          .sidebar-header {
            padding: 16px; display: flex; align-items: center; justify-content: space-between;
            border-bottom: 1px solid var(--sidebar-border);
          }
          .brand-badge {
            display: flex; align-items: center; gap: 10px; font-weight: 700;
            font-size: 15px; letter-spacing: 0.02em;
          }
          .brand-icon {
            width: 28px; height: 28px; border-radius: 8px;
            background: linear-gradient(135deg, #8b5cf6 0%, #ec4899 100%);
            display: flex; align-items: center; justify-content: center;
            color: #fff; font-weight: 900; font-size: 15px;
            box-shadow: 0 2px 10px var(--accent-glow);
          }
          .brand-tag {
            font-size: 10px; font-weight: 600; padding: 2px 6px; border-radius: 4px;
            background: var(--accent-subtle); color: var(--accent); border: 1px solid rgba(139, 92, 246, 0.3);
            text-transform: uppercase; letter-spacing: .08em;
          }
          .sidebar-btn {
            background: transparent; border: 0; color: var(--text-muted);
            cursor: pointer; padding: 6px; border-radius: 6px; display: flex; align-items: center;
          }
          .sidebar-btn:hover { background: var(--card-border); color: var(--text); }
          .new-chat-btn {
            margin: 14px 16px 8px; padding: 10px 14px; background: #191c2b;
            border: 1px solid var(--card-border); border-radius: var(--radius-md);
            color: var(--text); font-size: 13.5px; font-weight: 600; cursor: pointer;
            display: flex; align-items: center; justify-content: space-between;
            transition: all .15s ease;
          }
          .new-chat-btn:hover { background: #22263a; border-color: var(--accent); }
          .kbd {
            font-size: 11px; font-family: var(--font-mono); color: var(--text-dim);
            background: #11131c; padding: 2px 6px; border-radius: 4px; border: 1px solid var(--border-subtle);
          }
          .search-wrap { padding: 0 16px 12px; }
          .search-input {
            width: 100%; background: #131520; border: 1px solid var(--sidebar-border);
            border-radius: var(--radius-sm); padding: 7px 10px; color: var(--text);
            font-size: 12.5px; outline: none; transition: border-color .15s;
          }
          .search-input:focus { border-color: var(--accent); }
          .search-input::placeholder { color: var(--text-dim); }

          .sessions-list {
            flex: 1; overflow-y: auto; padding: 0 10px; display: flex; flex-direction: column; gap: 3px;
          }
          .sessions-section-label {
            font-size: 11px; text-transform: uppercase; letter-spacing: .08em;
            color: var(--text-dim); padding: 8px 10px 4px; font-weight: 600;
          }
          .session-item {
            display: flex; align-items: center; justify-content: space-between;
            padding: 8px 10px; border-radius: var(--radius-md); cursor: pointer;
            color: var(--text-muted); font-size: 13px; position: relative;
            transition: background .15s, color .15s;
          }
          .session-item:hover { background: var(--card); color: var(--text); }
          .session-item.active { background: #1a1d2e; color: #fff; font-weight: 500; border-left: 2px solid var(--accent); }
          .session-title { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; margin-right: 6px; }
          .session-actions { display: none; align-items: center; gap: 4px; }
          .session-item:hover .session-actions { display: flex; }
          .item-action-btn {
            background: transparent; border: 0; color: var(--text-dim);
            padding: 3px; border-radius: 4px; cursor: pointer; display: flex;
          }
          .item-action-btn:hover { color: var(--text); background: var(--border); }
          .item-action-btn.danger:hover { color: var(--red); }

          .sidebar-footer {
            padding: 14px 16px; border-top: 1px solid var(--sidebar-border);
            display: flex; flex-direction: column; gap: 10px; background: #0a0b10;
          }
          .workspace-pill {
            display: flex; align-items: center; gap: 8px; font-size: 12px;
            color: var(--text-muted); overflow: hidden;
          }
          .workspace-name {
            font-family: var(--font-mono); color: var(--text); font-weight: 500;
            overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
          }
          .branch-badge {
            font-size: 10.5px; font-family: var(--font-mono); background: #1a1f2e;
            padding: 1px 6px; border-radius: 4px; color: var(--cyan); border: 1px solid #23344d;
          }
          .provider-status-card {
            display: flex; align-items: center; justify-content: space-between;
            background: #141622; border: 1px solid var(--card-border);
            border-radius: var(--radius-md); padding: 8px 12px; cursor: pointer;
            transition: border-color .15s;
          }
          .provider-status-card:hover { border-color: var(--accent); }
          .provider-info { display: flex; align-items: center; gap: 8px; font-size: 12px; }
          .status-dot { width: 8px; height: 8px; border-radius: 50%; }
          .status-dot.green { background: var(--green); box-shadow: 0 0 8px var(--green); }
          .status-dot.amber { background: var(--amber); box-shadow: 0 0 8px var(--amber); }

          /* Main chat area */
          main.chat-main {
            flex: 1; display: flex; flex-direction: column; height: 100%;
            min-width: 0; background: var(--bg); position: relative;
          }

          /* Header */
          header.chat-header {
            height: 56px; min-height: 56px; border-bottom: 1px solid var(--border-subtle);
            padding: 0 24px; display: flex; align-items: center; justify-content: space-between;
            background: rgba(9, 9, 11, 0.85); backdrop-filter: blur(12px); z-index: 10;
          }
          .header-left { display: flex; align-items: center; gap: 14px; min-width: 0; }
          .header-tabs { display: flex; align-items: center; gap: 4px; background: #131624; padding: 3px 4px; border-radius: var(--radius-sm); border: 1px solid var(--card-border); }
          .header-tab-btn { background: transparent; border: 0; color: var(--text-muted); font-size: 11.5px; font-weight: 500; padding: 4px 10px; border-radius: 4px; cursor: pointer; transition: all .15s; }
          .header-tab-btn:hover { color: var(--text); background: rgba(255,255,255,0.05); }
          .header-tab-btn.active { background: var(--accent); color: #fff; font-weight: 600; }
          .header-title-wrap { display: flex; align-items: center; gap: 8px; min-width: 0; }
          .header-title {
            font-size: 14px; font-weight: 600; color: var(--text);
            overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
          }
          .header-right { display: flex; align-items: center; gap: 10px; }
          .header-btn {
            background: #131520; border: 1px solid var(--card-border); color: var(--text-muted);
            border-radius: var(--radius-sm); padding: 6px 12px; font-size: 12.5px;
            cursor: pointer; display: flex; align-items: center; gap: 6px; font-weight: 500;
            transition: all .15s;
          }
          .header-btn:hover { background: #1a1e2f; color: var(--text); border-color: var(--border); }
          .model-selector-btn {
            background: #141726; border: 1px solid var(--card-border); color: var(--text);
            border-radius: var(--radius-sm); padding: 6px 12px; font-size: 12px;
            font-family: var(--font-mono); cursor: pointer; display: flex; align-items: center; gap: 8px;
          }
          .model-selector-btn:hover { border-color: var(--accent); }

          /* Messages Container */
          .messages-viewport {
            flex: 1; overflow-y: auto; padding: 24px 20px 180px; display: flex;
            flex-direction: column; align-items: center; scroll-behavior: smooth;
          }
          .messages-content { width: 100%; max-width: 860px; display: flex; flex-direction: column; gap: 24px; }

          /* Empty State / Welcome Hero */
          .empty-hero {
            display: flex; flex-direction: column; align-items: center; text-align: center;
            padding: 48px 16px 24px; max-width: 720px; margin: auto;
          }
          .hero-brand-mark {
            width: 64px; height: 64px; border-radius: 16px;
            background: rgba(16, 185, 129, 0.12);
            border: 1px solid rgba(16, 185, 129, 0.35);
            display: flex; align-items: center; justify-content: center;
            box-shadow: 0 0 35px rgba(16, 185, 129, 0.25); margin-bottom: 20px;
          }
          .hero-title { font-size: 26px; font-weight: 700; letter-spacing: -0.02em; margin-bottom: 8px; }
          .hero-desc { font-size: 14.5px; color: var(--text-muted); line-height: 1.6; max-width: 540px; margin-bottom: 24px; }
          .hero-meta-pills { display: flex; flex-wrap: wrap; justify-content: center; gap: 8px; margin-bottom: 36px; }
          .meta-pill {
            display: flex; align-items: center; gap: 6px; font-size: 11.5px;
            font-family: var(--font-mono); background: #131622; border: 1px solid var(--card-border);
            padding: 4px 10px; border-radius: 20px; color: var(--text-muted);
          }
          .meta-pill b { color: var(--text); }

          .starter-cards-grid {
            display: grid; grid-template-columns: repeat(2, 1fr); gap: 12px; width: 100%; text-align: left;
          }
          @media (max-width: 680px) { .starter-cards-grid { grid-template-columns: 1fr; } }
          .starter-card {
            background: var(--card); border: 1px solid var(--card-border);
            border-radius: var(--radius-lg); padding: 16px; cursor: pointer;
            transition: all .2s ease; display: flex; flex-direction: column; gap: 6px;
          }
          .starter-card:hover {
            background: var(--card-hover); border-color: var(--accent);
            transform: translateY(-2px); box-shadow: 0 6px 20px rgba(0,0,0,0.3);
          }
          .starter-header { display: flex; align-items: center; gap: 8px; font-weight: 600; font-size: 14px; color: var(--text); }
          .starter-desc { font-size: 12.5px; color: var(--text-muted); line-height: 1.45; }

          /* Message Turns */
          .turn-row { width: 100%; display: flex; flex-direction: column; gap: 10px; }
          .turn-user { align-self: flex-end; max-width: 82%; }
          .user-bubble {
            background: #1e2235; border: 1px solid #2e3552; border-radius: 18px 18px 4px 18px;
            padding: 12px 18px; color: var(--text); font-size: 14px; line-height: 1.55;
            white-space: pre-wrap; word-break: break-word; box-shadow: 0 2px 10px rgba(0,0,0,0.2);
          }
          .turn-assistant { align-self: flex-start; width: 100%; display: flex; gap: 14px; }
          .assistant-avatar {
            width: 32px; height: 32px; min-width: 32px; border-radius: 10px;
            background: linear-gradient(135deg, #8b5cf6 0%, #ec4899 100%);
            display: flex; align-items: center; justify-content: center;
            color: #fff; font-weight: 800; font-size: 14px; margin-top: 2px;
            box-shadow: 0 2px 10px var(--accent-glow);
          }
          .assistant-body { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 14px; }

          /* Trace / Reasoning Block */
          .trace-accordion {
            background: #111420; border: 1px solid var(--card-border);
            border-radius: var(--radius-md); overflow: hidden; font-size: 12.5px;
          }
          .trace-summary {
            padding: 8px 12px; display: flex; align-items: center; justify-content: space-between;
            cursor: pointer; color: var(--text-muted); user-select: none;
          }
          .trace-summary:hover { color: var(--text); background: rgba(255,255,255,0.02); }
          .trace-title { display: flex; align-items: center; gap: 8px; font-weight: 500; }
          .trace-spinner {
            width: 12px; height: 12px; border: 2px solid var(--accent); border-top-color: transparent;
            border-radius: 50%; animation: spin .8s linear infinite;
          }
          .trace-content {
            padding: 10px 12px; border-top: 1px solid var(--border-subtle);
            font-family: var(--font-mono); font-size: 12px; color: var(--text-dim); line-height: 1.5;
            background: #0d0f17;
          }

          /* Tool Call Card */
          .tool-card {
            background: #111420; border: 1px solid var(--card-border);
            border-radius: var(--radius-md); overflow: hidden; font-size: 12.5px;
            transition: border-color .15s;
          }
          .tool-card.running { border-color: #3b4566; }
          .tool-card.ok { border-color: var(--green-border); }
          .tool-card.bad { border-color: var(--red-border); }
          .tool-header {
            padding: 9px 12px; display: flex; align-items: center; justify-content: space-between;
            cursor: pointer; user-select: none;
          }
          .tool-header:hover { background: rgba(255,255,255,0.02); }
          .tool-identity { display: flex; align-items: center; gap: 8px; }
          .tool-icon-badge {
            width: 22px; height: 22px; border-radius: 6px; display: flex;
            align-items: center; justify-content: center; background: #1a1e30; color: var(--text-muted);
          }
          .tool-name { font-family: var(--font-mono); font-weight: 600; color: var(--text); }
          .tool-args-preview {
            font-family: var(--font-mono); font-size: 11.5px; color: var(--text-dim);
            max-width: 360px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
          }
          .tool-badge {
            display: flex; align-items: center; gap: 6px; font-size: 11px;
            font-family: var(--font-mono); padding: 2px 8px; border-radius: 12px;
          }
          .tool-badge.running { background: var(--accent-subtle); color: var(--accent); }
          .tool-badge.ok { background: var(--green-subtle); color: var(--green); }
          .tool-badge.bad { background: var(--red-subtle); color: var(--red); }
          .tool-body {
            border-top: 1px solid var(--border-subtle); padding: 10px 12px;
            background: #0a0c12; font-family: var(--font-mono); font-size: 12px;
            display: flex; flex-direction: column; gap: 8px;
          }
          .tool-section-label { font-size: 10.5px; text-transform: uppercase; color: var(--text-dim); letter-spacing: .06em; }
          .tool-code-box {
            background: #11141f; border: 1px solid #1f2438; border-radius: 6px;
            padding: 8px 10px; overflow-x: auto; white-space: pre-wrap; word-break: break-all;
            color: #d1d7e5; max-height: 240px;
          }

          /* Markdown content */
          .prose { font-size: 14.5px; line-height: 1.65; color: var(--text); word-break: break-word; }
          .prose p { margin-bottom: 12px; }
          .prose p:last-child { margin-bottom: 0; }
          .prose h1, .prose h2, .prose h3, .prose h4 { margin: 18px 0 8px; font-weight: 700; color: #fff; }
          .prose h1 { font-size: 20px; }
          .prose h2 { font-size: 17px; }
          .prose h3 { font-size: 15px; }
          .prose ul, .prose ol { margin: 8px 0 12px 22px; display: flex; flex-direction: column; gap: 4px; }
          .prose li { line-height: 1.55; }
          .prose blockquote {
            border-left: 3px solid var(--accent); padding-left: 12px; color: var(--text-muted);
            margin: 12px 0; font-style: italic;
          }
          .prose code:not(pre code) {
            font-family: var(--font-mono); font-size: 12.5px; background: #191c2b;
            padding: 2px 6px; border-radius: 5px; color: #e2e8f0; border: 1px solid #23283d;
          }
          .prose pre {
            background: #0d0f17; border: 1px solid var(--card-border); border-radius: var(--radius-md);
            margin: 14px 0; overflow: hidden;
          }
          .code-block-header {
            display: flex; align-items: center; justify-content: space-between;
            padding: 6px 12px; background: #131622; border-bottom: 1px solid #1a1e30;
            font-family: var(--font-mono); font-size: 11.5px; color: var(--text-dim);
          }
          .code-copy-btn {
            background: transparent; border: 0; color: var(--text-muted);
            font-size: 11px; cursor: pointer; display: flex; align-items: center; gap: 4px;
            padding: 2px 6px; border-radius: 4px;
          }
          .code-copy-btn:hover { background: #1f2538; color: var(--text); }
          .prose pre code {
            display: block; padding: 12px; font-family: var(--font-mono);
            font-size: 13px; line-height: 1.5; overflow-x: auto; color: #dbe2f0;
          }
          .prose table {
            width: 100%; border-collapse: collapse; margin: 14px 0; font-size: 13px;
          }
          .prose th, .prose td {
            border: 1px solid var(--card-border); padding: 8px 12px; text-align: left;
          }
          .prose th { background: #141624; color: #fff; font-weight: 600; }
          .prose tr:nth-child(even) { background: rgba(255,255,255,0.015); }

          /* Streaming cursor */
          .streaming-cursor {
            display: inline-block; width: 6px; height: 15px; background: var(--accent);
            margin-left: 4px; vertical-align: text-bottom; animation: pulse-cursor .9s ease-in-out infinite;
          }

          /* Floating Prompt Dock */
          .prompt-dock-container {
            position: absolute; bottom: 0; left: 0; right: 0;
            padding: 0 20px 20px; display: flex; justify-content: center;
            background: linear-gradient(to top, var(--bg) 75%, transparent 100%);
            pointer-events: none; z-index: 15;
          }
          .prompt-box {
            width: 100%; max-width: 860px; background: #111420;
            border: 1px solid #23283d; border-radius: var(--radius-xl);
            box-shadow: 0 8px 32px rgba(0,0,0,0.45); pointer-events: auto;
            display: flex; flex-direction: column; overflow: visible; position: relative;
            transition: border-color .15s, box-shadow .15s;
          }
          .prompt-box:focus-within {
            border-color: var(--accent);
            box-shadow: 0 8px 32px rgba(0,0,0,0.5), 0 0 0 1px var(--accent-glow);
          }
          .prompt-textarea {
            width: 100%; min-height: 52px; max-height: 180px; background: transparent;
            border: 0; color: var(--text); padding: 14px 18px 8px; font-size: 14.5px;
            line-height: 1.5; resize: none; outline: none; overflow-y: auto;
          }
          .prompt-textarea::placeholder { color: var(--text-dim); }
          .prompt-toolbar {
            padding: 8px 14px 10px; display: flex; align-items: center; justify-content: space-between;
          }
          .toolbar-left { display: flex; align-items: center; gap: 8px; }
          .toolbar-pill {
            background: #171b2b; border: 1px solid #252b42; border-radius: 8px;
            color: var(--text-muted); font-size: 12px; padding: 4px 8px;
            cursor: pointer; display: flex; align-items: center; gap: 5px;
            transition: all .15s;
          }
          .toolbar-pill:hover { background: #20263d; color: var(--text); border-color: var(--border); }
          .toolbar-right { display: flex; align-items: center; gap: 10px; }
          .prompt-hint { font-size: 11.5px; color: var(--text-dim); }
          @media (max-width: 640px) { .prompt-hint { display: none; } }
          .send-btn {
            width: 34px; height: 34px; border-radius: 10px; background: var(--accent);
            border: 0; color: #fff; display: flex; align-items: center; justify-content: center;
            cursor: pointer; transition: background .15s, transform .1s;
          }
          .send-btn:hover:not(:disabled) { background: var(--accent-hover); transform: scale(1.04); }
          .send-btn:disabled { opacity: .4; cursor: default; }

          /* Autocomplete floating menu */
          .autocomplete-menu {
            position: absolute; bottom: calc(100% + 8px); left: 0; right: 0;
            background: #131726; border: 1px solid var(--border); border-radius: var(--radius-lg);
            max-height: 240px; overflow-y: auto; box-shadow: 0 12px 36px rgba(0,0,0,0.6);
            display: flex; flex-direction: column; padding: 6px; z-index: 30;
          }
          .autocomplete-item {
            display: flex; align-items: center; justify-content: space-between;
            padding: 8px 12px; border-radius: var(--radius-sm); cursor: pointer;
            color: var(--text); font-size: 13px; font-family: var(--font-mono);
          }
          .autocomplete-item:hover, .autocomplete-item.selected {
            background: #20273d; color: #fff;
          }
          .autocomplete-desc { font-size: 11.5px; color: var(--text-muted); font-family: var(--font-sans); }

          /* Modals */
          .modal-backdrop {
            position: fixed; inset: 0; background: rgba(0,0,0,0.7); backdrop-filter: blur(4px);
            display: flex; align-items: center; justify-content: center; z-index: 50; padding: 20px;
          }
          .modal-window {
            background: #111420; border: 1px solid var(--card-border); border-radius: var(--radius-xl);
            width: 100%; max-width: 580px; max-height: 85vh; display: flex; flex-direction: column;
            overflow: hidden; box-shadow: 0 20px 50px rgba(0,0,0,0.6); animation: modal-in .15s ease-out;
          }
          .modal-header {
            padding: 16px 20px; border-bottom: 1px solid var(--sidebar-border);
            display: flex; align-items: center; justify-content: space-between;
          }
          .modal-title { font-size: 16px; font-weight: 700; color: #fff; }
          .modal-body { padding: 20px; overflow-y: auto; display: flex; flex-direction: column; gap: 16px; }
          .modal-footer {
            padding: 14px 20px; border-top: 1px solid var(--sidebar-border);
            display: flex; justify-content: flex-end; gap: 10px; background: #0c0e17;
          }
          .form-group { display: flex; flex-direction: column; gap: 6px; }
          .form-label { font-size: 12.5px; font-weight: 600; color: var(--text-muted); }
          .form-input {
            background: #161928; border: 1px solid var(--card-border); border-radius: var(--radius-sm);
            padding: 8px 12px; color: var(--text); font-size: 13px; outline: none;
          }
          .form-input:focus { border-color: var(--accent); }
          .form-select {
            background: #161928; border: 1px solid var(--card-border); border-radius: var(--radius-sm);
            padding: 8px 12px; color: var(--text); font-size: 13px; outline: none;
          }
          .btn-primary {
            background: var(--accent); color: #fff; border: 0; padding: 8px 16px;
            border-radius: var(--radius-sm); font-weight: 600; font-size: 13px; cursor: pointer;
          }
          .btn-primary:hover { background: var(--accent-hover); }
          .btn-secondary {
            background: #1c2032; color: var(--text); border: 1px solid var(--card-border);
            padding: 8px 16px; border-radius: var(--radius-sm); font-weight: 500; font-size: 13px; cursor: pointer;
          }
          .btn-secondary:hover { background: #252b42; }

          @keyframes spin { 100% { transform: rotate(360deg); } }
          @keyframes pulse-cursor { 0%, 100% { opacity: 1; } 50% { opacity: 0; } }
          @keyframes modal-in { from { opacity: 0; transform: scale(.96); } to { opacity: 1; transform: scale(1); } }
        </style>
        </head>
        <body>
        <div id="root"></div>

        <script type="module">
        import React, { useState, useEffect, useRef, useCallback } from "https://esm.sh/react@19";
        import { createRoot } from "https://esm.sh/react-dom@19/client";
        import { marked } from "https://esm.sh/marked@15.0.0";

        // Fallback markdown converter
        try {
          marked.use({
            breaks: true,
            gfm: true,
            renderer: {
              code({ text, lang }) {
                const language = lang || "code";
                const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
                return `<div class="code-block-container" style="background:#0d0f17; border:1px solid var(--card-border); border-radius:var(--radius-md); margin:14px 0; overflow:hidden;">
                  <div class="code-block-header" style="display:flex; align-items:center; justify-content:space-between; padding:6px 12px; background:#131622; border-bottom:1px solid #1a1e30; font-family:var(--font-mono); font-size:11.5px; color:var(--text-dim);">
                    <span>${language}</span>
                    <button class="code-copy-btn" style="background:transparent; border:0; color:var(--text-muted); font-size:11px; cursor:pointer; display:flex; align-items:center; gap:4px; padding:2px 6px; border-radius:4px;" onclick="navigator.clipboard.writeText(decodeURIComponent('${encodeURIComponent(text)}')); this.textContent = '✓ Copied!'; setTimeout(() => this.textContent = 'Copy', 1500);">Copy</button>
                  </div>
                  <pre style="margin:0; background:transparent; border:0; border-radius:0;"><code style="display:block; padding:12px; font-family:var(--font-mono); font-size:13px; line-height:1.5; overflow-x:auto; color:#dbe2f0;">${esc(text)}</code></pre>
                </div>`;
              }
            }
          });
        } catch {}

        function parseMd(text) {
          try {
            return marked.parse(text);
          } catch {
            const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;");
            return esc(text).replace(/\n/g, "<br/>");
          }
        }

        // SVG Icons
        const Icon = {
          Send: () => React.createElement("svg", { width: 16, height: 16, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2.2, strokeLinecap: "round", strokeLinejoin: "round" },
            React.createElement("line", { x1: "12", y1: "19", x2: "12", y2: "5" }),
            React.createElement("polyline", { points: "5 12 12 5 19 12" })),
          Stop: () => React.createElement("svg", { width: 14, height: 14, viewBox: "0 0 24 24", fill: "currentColor" },
            React.createElement("rect", { x: "4", y: "4", width: "16", height: "16", rx: "2" })),
          Plus: () => React.createElement("svg", { width: 14, height: 14, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round" },
            React.createElement("line", { x1: "12", y1: "5", x2: "12", y2: "19" }),
            React.createElement("line", { x1: "5", y1: "12", x2: "19", y2: "12" })),
          Trash: () => React.createElement("svg", { width: 13, height: 13, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round" },
            React.createElement("polyline", { points: "3 6 5 6 21 6" }),
            React.createElement("path", { d: "M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" })),
          Edit: () => React.createElement("svg", { width: 13, height: 13, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round" },
            React.createElement("path", { d: "M17 3a2.828 2.828 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5L17 3z" })),
          Terminal: () => React.createElement("svg", { width: 13, height: 13, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round" },
            React.createElement("polyline", { points: "4 17 10 11 4 5" }),
            React.createElement("line", { x1: "12", y1: "19", x2: "20", y2: "19" })),
          File: () => React.createElement("svg", { width: 13, height: 13, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round" },
            React.createElement("path", { d: "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z" }),
            React.createElement("polyline", { points: "14 2 14 8 20 8" })),
          Folder: () => React.createElement("svg", { width: 13, height: 13, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round" },
            React.createElement("path", { d: "M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z" })),
          Sidebar: () => React.createElement("svg", { width: 16, height: 16, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round" },
            React.createElement("rect", { x: "3", y: "3", width: "18", height: "18", rx: "2" }),
            React.createElement("line", { x1: "9", y1: "3", x2: "9", y2: "21" })),
          Settings: () => React.createElement("svg", { width: 14, height: 14, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round" },
            React.createElement("circle", { cx: "12", cy: "12", r: "3" }),
            React.createElement("path", { d: "M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1 0 2.83 2 2 0 0 1-2.83 0l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-2 2 2 2 0 0 1-2-2v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83 0 2 2 0 0 1 0-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1-2-2 2 2 0 0 1 2-2h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 0-2.83 2 2 0 0 1 2.83 0l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 2-2 2 2 0 0 1 2 2v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 0 2 2 0 0 1 0 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 2 2 2 2 0 0 1-2 2h-.09a1.65 1.65 0 0 0-1.51 1z" })),
          ChevronRight: () => React.createElement("svg", { width: 14, height: 14, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round" },
            React.createElement("polyline", { points: "9 18 15 12 9 6" })),
          ChevronDown: () => React.createElement("svg", { width: 14, height: 14, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round" },
            React.createElement("polyline", { points: "6 9 12 15 18 9" })),
          Check: () => React.createElement("svg", { width: 13, height: 13, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2.5, strokeLinecap: "round" },
            React.createElement("polyline", { points: "20 6 9 17 4 12" })),
          Copy: () => React.createElement("svg", { width: 13, height: 13, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, strokeLinecap: "round" },
            React.createElement("rect", { x: "9", y: "9", width: "13", height: "13", rx: "2" }),
            React.createElement("path", { d: "M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" })),
          Dino: (props = {}) => React.createElement("svg", {
            width: props.width || 24,
            height: props.height || 24,
            viewBox: "0 0 24 24",
            fill: "currentColor",
            style: { color: "#10b981", flexShrink: 0, ...(props.style || {}) }
          },
            React.createElement("path", {
              d: "M21 4h-6v2h-2v2h-2V6H9V4H5v4h2v4h2v2H7v2H5v2H3v4h4v-2h2v-2h2v-2h2v4h2v-4h2v-2h2v-4h2V8h-2V6h2V4z"
            })),
        };

        const cleanTitle = (t) => {
          if (!t) return "Untitled chat";
          const stripped = t.replace(/\x1b\[[0-9;]*[a-zA-Z]/g, "").replace(/[\x00-\x1f]/g, "").trim();
          return stripped.length > 0 ? stripped : "Untitled chat";
        };

        const SLASH_COMMANDS = [
          { cmd: "/help", desc: "Show tips, keys, and commands" },
          { cmd: "/model", desc: "Switch the active LLM model" },
          { cmd: "/connect", desc: "Connect or update API key" },
          { cmd: "/providers", desc: "View status of all LLM providers" },
          { cmd: "/tools", desc: "Inspect workspace tools (read, write, edit, run)" },
          { cmd: "/compact", desc: "Summarize the context into a checkpoint" },
          { cmd: "/clear", desc: "Clear current conversation" },
          { cmd: "/new", desc: "Start a new chat session" },
        ];

        function App() {
          const [messages, setMessages] = useState([]);
          const [sessions, setSessions] = useState([]);
          const [sessionId, setSessionId] = useState(null);
          const [sessionTitle, setSessionTitle] = useState("");
          const [tools, setTools] = useState([]);
          const [status, setStatus] = useState(null);
          const [files, setFiles] = useState([]);
          const [input, setInput] = useState("");
          const [busy, setBusy] = useState(false);
          const [sidebarOpen, setSidebarOpen] = useState(true);
          const [sessionFilter, setSessionFilter] = useState("");
          const [modal, setModal] = useState(null); // null | "connect" | "tools" | "rename"
          const [renameTitle, setRenameTitle] = useState("");
          const [renameTargetId, setRenameTargetId] = useState(null);

          // Provider Connect Form State
          const [selectedProvider, setSelectedProvider] = useState("openai");
          const [apiKeyInput, setApiKeyInput] = useState("");
          const [baseUrlInput, setBaseUrlInput] = useState("");
          const [modelInput, setModelInput] = useState("");
          const [connectTesting, setConnectTesting] = useState(false);
          const [connectResult, setConnectResult] = useState(null);

          // Autocomplete state
          const [autocomplete, setAutocomplete] = useState(null); // { type: 'slash' | 'file', items: [], selectedIndex: 0 }

          const endRef = useRef(null);
          const textareaRef = useRef(null);

          const loadStatus = useCallback(async () => {
            try {
              const res = await fetch("/api/status");
              if (res.ok) {
                const data = await res.json();
                setStatus(data);
                if (data.provider) setSelectedProvider(data.provider);
              }
            } catch {}
          }, []);

          const loadSessions = useCallback(async () => {
            try {
              const res = await fetch("/api/sessions");
              if (res.ok) setSessions(await res.json());
            } catch {}
          }, []);

          const loadTools = useCallback(async () => {
            try {
              const res = await fetch("/api/tools");
              if (res.ok) setTools(await res.json());
            } catch {}
          }, []);

          const loadFiles = useCallback(async () => {
            try {
              const res = await fetch("/api/workspace/files");
              if (res.ok) setFiles(await res.json());
            } catch {}
          }, []);

          useEffect(() => {
            loadStatus();
            loadSessions();
            loadTools();
            loadFiles();
          }, [loadStatus, loadSessions, loadTools, loadFiles]);

          // Auto-scroll when messages update
          useEffect(() => {
            endRef.current?.scrollIntoView({ behavior: "smooth" });
          }, [messages, busy]);

          useEffect(() => {
            const onKey = (e) => {
              if ((e.metaKey || e.ctrlKey) && e.key === "k") {
                e.preventDefault();
                newChat();
              }
            };
            window.addEventListener("keydown", onKey);
            return () => window.removeEventListener("keydown", onKey);
          }, []);

          // Handle input resizing
          const handleInput = (e) => {
            const val = e.target.value;
            setInput(val);
            e.target.style.height = "auto";
            e.target.style.height = Math.min(e.target.scrollHeight, 180) + "px";

            // Check autocomplete triggers
            const cursor = e.target.selectionStart;
            const before = val.slice(0, cursor);

            // Slash command at start of input
            if (/^\/[a-zA-Z0-9_-]*$/.test(before)) {
              const filter = before.toLowerCase();
              const items = SLASH_COMMANDS.filter(s => s.cmd.startsWith(filter));
              setAutocomplete(items.length > 0 ? { type: "slash", items, selectedIndex: 0 } : null);
              return;
            }

            // File mention trigger '@'
            const atMatch = before.match(/@([a-zA-Z0-9_\-./]*)$/);
            if (atMatch) {
              const query = atMatch[1].toLowerCase();
              const items = files.filter(f => f.path.toLowerCase().includes(query)).slice(0, 8);
              setAutocomplete(items.length > 0 ? { type: "file", items, selectedIndex: 0, queryLength: atMatch[0].length } : null);
              return;
            }

            setAutocomplete(null);
          };

          const handleKeyDown = (e) => {
            if (autocomplete) {
              if (e.key === "ArrowDown") {
                e.preventDefault();
                setAutocomplete(a => ({ ...a, selectedIndex: (a.selectedIndex + 1) % a.items.length }));
                return;
              }
              if (e.key === "ArrowUp") {
                e.preventDefault();
                setAutocomplete(a => ({ ...a, selectedIndex: (a.selectedIndex - 1 + a.items.length) % a.items.length }));
                return;
              }
              if (e.key === "Enter" || e.key === "Tab") {
                e.preventDefault();
                applyAutocomplete(autocomplete.items[autocomplete.selectedIndex]);
                return;
              }
              if (e.key === "Escape") {
                setAutocomplete(null);
                return;
              }
            }

            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              send();
            }
          };

          const applyAutocomplete = (item) => {
            if (autocomplete.type === "slash") {
              setInput(item.cmd + " ");
              setAutocomplete(null);
              textareaRef.current?.focus();
            } else if (autocomplete.type === "file") {
              const cursor = textareaRef.current.selectionStart;
              const before = input.slice(0, cursor - autocomplete.queryLength);
              const after = input.slice(cursor);
              setInput(before + "@" + item.path + " " + after);
              setAutocomplete(null);
              textareaRef.current?.focus();
            }
          };

          const newChat = () => {
            setSessionId(null);
            setSessionTitle("");
            setMessages([]);
            setInput("");
            if (textareaRef.current) textareaRef.current.style.height = "auto";
          };

          const openSession = async (id) => {
            setSessionId(id);
            const found = sessions.find(s => s.id === id);
            setSessionTitle(found ? found.title : id);
            try {
              const res = await fetch(`/api/sessions/${id}/messages`);
              if (res.ok) {
                const rows = await res.json();
                setMessages(rows.map(r => ({
                  role: r.role,
                  content: r.content,
                  name: r.name,
                  toolCalls: r.toolCalls || [],
                  steps: []
                })));
              }
            } catch {}
          };

          const deleteSession = async (e, id) => {
            e.stopPropagation();
            if (!confirm("Delete this session?")) return;
            try {
              await fetch(`/api/sessions/${id}`, { method: "DELETE" });
              setSessions(s => s.filter(x => x.id !== id));
              if (sessionId === id) newChat();
            } catch {}
          };

          const openRenameModal = (e, session) => {
            e.stopPropagation();
            setRenameTargetId(session.id);
            setRenameTitle(session.title);
            setModal("rename");
          };

          const saveRename = async () => {
            if (!renameTargetId || !renameTitle.trim()) return;
            try {
              await fetch(`/api/sessions/${renameTargetId}`, {
                method: "PATCH",
                headers: { "content-type": "application/json" },
                body: JSON.stringify({ title: renameTitle.trim() })
              });
              setSessions(s => s.map(x => x.id === renameTargetId ? { ...x, title: renameTitle.trim() } : x));
              if (sessionId === renameTargetId) setSessionTitle(renameTitle.trim());
              setModal(null);
            } catch {}
          };

          const send = async (customText) => {
            const text = (customText || input).trim();
            if (!text) return;

            if (text === "/compact") {
              setInput("");
              await compactNow();
              return;
            }

            if (text === "/clear") {
              setInput("");
              setMessages([]);
              return;
            }

            // While the agent runs, a new message steers it rather than starting a second run.
            if (busy) {
              setInput("");
              if (textareaRef.current) textareaRef.current.style.height = "auto";
              setMessages(m => [...m, { role: "user", content: `(steering) ${text}` }]);
              try {
                await fetch("/api/chat/steer", {
                  method: "POST",
                  headers: { "content-type": "application/json" },
                  body: JSON.stringify({ sessionId, message: text })
                });
              } catch { /* the run may have finished; the message is simply lost */ }
              return;
            }

            // Intercept slash commands locally if user ran /clear, /new, etc.
            if (text === "/clear") { setMessages([]); setInput(""); return; }
            if (text === "/new") { newChat(); return; }
            if (text === "/tools") { setModal("tools"); setInput(""); return; }
            if (text === "/connect" || text === "/providers") { setModal("connect"); setInput(""); return; }

            setInput("");
            setBusy(true);
            setAutocomplete(null);
            if (textareaRef.current) textareaRef.current.style.height = "auto";

            // Append user message
            const userMsg = { role: "user", content: text };
            setMessages(m => [...m, userMsg]);

            try {
              const res = await fetch("/api/chat", {
                method: "POST",
                headers: { "content-type": "application/json" },
                body: JSON.stringify({ message: text, sessionId, provider: status?.provider, model: status?.model })
              });

              if (!res.ok || !res.body) {
                setMessages(m => [...m, { role: "assistant", content: `**Error:** server returned HTTP ${res.status}` }]);
                setBusy(false);
                return;
              }

              const reader = res.body.getReader();
              const decoder = new TextDecoder();
              let buffer = "";
              let assistantText = "";
              let activeTools = [];
              let activeSteps = [];

              // Placeholder for the streaming assistant response
              setMessages(m => [...m, {
                role: "assistant",
                content: "",
                toolCalls: [],
                steps: [],
                isStreaming: true
              }]);

              const updateAssistant = () => {
                setMessages(m => {
                  const copy = [...m];
                  copy[copy.length - 1] = {
                    role: "assistant",
                    content: assistantText,
                    toolCalls: [...activeTools],
                    steps: [...activeSteps],
                    isStreaming: true
                  };
                  return copy;
                });
              };

              const flushFrame = (frame) => {
                if (!frame || frame === "[DONE]") return;
                let evt;
                try { evt = JSON.parse(frame); } catch { return; }

                if (evt.type === "turn_start") {
                  activeSteps.push(`Step ${evt.step}`);
                  updateAssistant();
                } else if (evt.type === "text") {
                  assistantText += evt.text;
                  updateAssistant();
                } else if (evt.type === "tool_start") {
                  activeTools.push({
                    id: evt.id,
                    name: evt.name,
                    args: evt.args,
                    state: "running",
                    expanded: false
                  });
                  updateAssistant();
                } else if (evt.type === "tool_end") {
                  const idx = activeTools.findIndex(t => t.id === evt.id) !== -1
                    ? activeTools.findIndex(t => t.id === evt.id)
                    : activeTools.length - 1;
                  if (idx >= 0 && activeTools[idx]) {
                    activeTools[idx] = {
                      ...activeTools[idx],
                      state: evt.success ? "ok" : "bad",
                      result: evt.result,
                      ms: evt.elapsedMs
                    };
                  }
                  updateAssistant();
                } else if (evt.type === "done") {
                  if (evt.sessionId) {
                    setSessionId(evt.sessionId);
                    loadSessions();
                  }
                } else if (evt.type === "compaction_start") {
                  activeSteps.push(`Compacting (${evt.reason})…`);
                  updateAssistant();
                } else if (evt.type === "compaction_end") {
                  activeSteps.push(evt.error
                    ? `Compaction failed: ${evt.error}`
                    : `Compacted ${evt.tokensBefore} → ${evt.estimatedTokensAfter} tokens`);
                  updateAssistant();
                } else if (evt.type === "retry_scheduled") {
                  activeSteps.push(`Retrying in ${Math.round(evt.delayMs)}ms (${evt.reason})`);
                  updateAssistant();
                } else if (evt.type === "error") {
                  assistantText += `\n\n**Error:** ${evt.message}`;
                  updateAssistant();
                }
              };

              while (true) {
                const { done, value } = await reader.read();
                if (done) break;
                buffer += decoder.decode(value, { stream: true });
                let idx;
                while ((idx = buffer.indexOf("\n\n")) >= 0) {
                  const chunk = buffer.slice(0, idx);
                  buffer = buffer.slice(idx + 2);
                  if (chunk.startsWith("data:")) flushFrame(chunk.slice(5).trim());
                }
              }

              // Finalize message
              setMessages(m => {
                const copy = [...m];
                if (copy.length > 0) {
                  copy[copy.length - 1] = {
                    ...copy[copy.length - 1],
                    isStreaming: false
                  };
                }
                return copy;
              });

            } catch (err) {
              setMessages(m => [...m, { role: "assistant", content: `**Network Error:** ${err.message}` }]);
            } finally {
              setBusy(false);
            }
          };

          const abortRun = async () => {
            try {
              await fetch("/api/chat/abort", {
                method: "POST",
                headers: { "content-type": "application/json" },
                body: JSON.stringify({ sessionId })
              });
            } catch { /* already finished */ }
          };

          // Compact on demand, mirroring the terminal's /compact.
          const compactNow = async () => {
            try {
              const res = await fetch("/api/chat/compact", {
                method: "POST",
                headers: { "content-type": "application/json" },
                body: JSON.stringify({ sessionId })
              });
              const data = await res.json();
              setMessages(m => [...m, {
                role: "assistant",
                content: res.ok
                  ? `**Context compacted.** ${data.tokensBefore} → ${data.estimatedTokensAfter} tokens.\n\n${data.summary}`
                  : `**Compaction skipped:** ${data.error ?? res.status}`
              }]);
              if (res.ok) loadMessages(sessionId);
            } catch (err) {
              setMessages(m => [...m, { role: "assistant", content: `**Compaction failed:** ${err.message}` }]);
            }
          };

          const handleConnect = async (e) => {
            e.preventDefault();
            setConnectTesting(true);
            setConnectResult(null);
            try {
              const res = await fetch("/api/providers/connect", {
                method: "POST",
                headers: { "content-type": "application/json" },
                body: JSON.stringify({
                  provider: selectedProvider,
                  apiKey: apiKeyInput,
                  baseUrl: baseUrlInput,
                  model: modelInput
                })
              });
              const data = await res.json();
              setConnectResult(data);
              await loadStatus();
            } catch (err) {
              setConnectResult({ ok: false, testError: err.message });
            } finally {
              setConnectTesting(false);
            }
          };

          const filteredSessions = sessions.filter(s =>
            !sessionFilter || (s.title || s.id).toLowerCase().includes(sessionFilter.toLowerCase()));

          return React.createElement("div", { className: "app-layout" },
            // Left Sidebar
            React.createElement("aside", { className: `sidebar ${sidebarOpen ? "" : "collapsed"}` },
              React.createElement("div", { className: "sidebar-header" },
                React.createElement("div", { className: "brand-badge" },
                  React.createElement(Icon.Dino, { width: 18, height: 18 }),
                  React.createElement("span", null, "Reptar Agent"),
                  React.createElement("span", { className: "brand-tag" }, "v0.2")
                ),
                React.createElement("button", {
                  className: "sidebar-btn",
                  title: "Close sidebar",
                  onClick: () => setSidebarOpen(false)
                }, React.createElement(Icon.Sidebar))
              ),
              React.createElement("button", { className: "new-chat-btn", onClick: newChat },
                React.createElement("span", { style: { display: "flex", alignItems: "center", gap: "8px" } },
                  React.createElement(Icon.Plus), "New chat"),
                React.createElement("span", { className: "kbd" }, "⌘K")
              ),
              React.createElement("div", { className: "search-wrap" },
                React.createElement("input", {
                  className: "search-input",
                  placeholder: "Search chats…",
                  value: sessionFilter,
                  onChange: e => setSessionFilter(e.target.value)
                })
              ),
              React.createElement("div", { className: "sessions-list" },
                React.createElement("div", { className: "sessions-section-label" }, "Saved Conversations"),
                filteredSessions.map(s => React.createElement("div", {
                  key: s.id,
                  className: `session-item ${s.id === sessionId ? "active" : ""}`,
                  onClick: () => openSession(s.id)
                },
                  React.createElement("span", { className: "session-title" }, cleanTitle(s.title || s.id)),
                  React.createElement("div", { className: "session-actions" },
                    React.createElement("button", {
                      className: "item-action-btn",
                      title: "Rename chat",
                      onClick: (e) => openRenameModal(e, s)
                    }, React.createElement(Icon.Edit)),
                    React.createElement("button", {
                      className: "item-action-btn danger",
                      title: "Delete chat",
                      onClick: (e) => deleteSession(e, s.id)
                    }, React.createElement(Icon.Trash))
                  )
                ))
              ),
              React.createElement("div", { className: "sidebar-footer" },
                React.createElement("div", { className: "workspace-pill" },
                  React.createElement(Icon.Folder),
                  React.createElement("span", { className: "workspace-name", title: status?.workspaceRoot },
                    status?.workspaceName || "workspace"),
                  status?.gitBranch && React.createElement("span", { className: "branch-badge" }, status.gitBranch)
                ),
                React.createElement("div", {
                  className: "provider-status-card",
                  onClick: () => setModal("connect")
                },
                  React.createElement("div", { className: "provider-info" },
                    React.createElement("div", { className: `status-dot ${status?.isOffline ? "amber" : "green"}` }),
                    React.createElement("span", { style: { fontWeight: 500 } },
                      status?.isOffline ? "Offline Echo Mode" : (status?.displayName || status?.provider || "Connected"))
                  ),
                  React.createElement(Icon.Settings)
                )
              )
            ),

            // Main Chat Area
            React.createElement("main", { className: "chat-main" },
              // Header
              React.createElement("header", { className: "chat-header" },
                React.createElement("div", { className: "header-left" },
                  !sidebarOpen && React.createElement("button", {
                    className: "sidebar-btn",
                    title: "Open sidebar",
                    onClick: () => setSidebarOpen(true)
                  }, React.createElement(Icon.Sidebar)),
                  React.createElement("div", { className: "header-tabs" },
                    [
                      { id: "chat", label: "1 Chat" },
                      { id: "sessions", label: "2 Sessions" },
                      { id: "tools", label: "3 Tools" },
                      { id: "providers", label: "4 Providers" }
                    ].map(t => React.createElement("button", {
                      key: t.id,
                      className: `header-tab-btn ${t.id === "chat" ? "active" : ""}`,
                      onClick: () => {
                        if (t.id === "tools") setModal("tools");
                        else if (t.id === "providers") setModal("connect");
                        else if (t.id === "sessions") setSidebarOpen(s => !s);
                      }
                    }, t.label))
                  ),
                  React.createElement("div", { className: "header-title-wrap" },
                    React.createElement("span", { className: "header-title" },
                      cleanTitle(sessionTitle || (sessionId ? `Session ${sessionId.slice(0, 10)}…` : "New chat")))
                  )
                ),
                React.createElement("div", { className: "header-right" },
                  React.createElement("button", {
                    className: "model-selector-btn",
                    onClick: () => setModal("connect")
                  },
                    React.createElement("span", { style: { color: "var(--accent)" } }, "⚡"),
                    React.createElement("span", null, status?.model || "default-model"),
                    React.createElement(Icon.ChevronDown)
                  ),
                  React.createElement("button", {
                    className: "header-btn",
                    onClick: () => setModal("tools")
                  },
                    React.createElement(Icon.Terminal),
                    React.createElement("span", null, `${tools.length} Tools`)
                  )
                )
              ),

              // Messages Viewport
              React.createElement("div", { className: "messages-viewport" },
                React.createElement("div", { className: "messages-content" },
                  messages.length === 0
                    // Empty State Hero
                    ? React.createElement("div", { className: "empty-hero" },
                        React.createElement("div", { className: "hero-brand-mark" }, React.createElement(Icon.Dino, { width: 36, height: 36 })),
                        React.createElement("h1", { className: "hero-title" }, "What can I build for you?"),
                        React.createElement("p", { className: "hero-desc" },
                          "Reptar Agent runs in-process with direct tool execution across your codebase. Native AOT & NetWasm ready."),
                        React.createElement("div", { className: "hero-meta-pills" },
                          React.createElement("div", { className: "meta-pill" },
                            React.createElement("span", null, "📁 Workspace:"),
                            React.createElement("b", null, status?.workspaceName || "reptar")
                          ),
                          React.createElement("div", { className: "meta-pill" },
                            React.createElement("span", null, "⚡ Model:"),
                            React.createElement("b", null, status?.model || "echo")
                          ),
                          React.createElement("div", { className: "meta-pill" },
                            React.createElement("span", null, "🛠️ Tools:"),
                            React.createElement("b", null, `${tools.length} mounted`)
                          )
                        ),
                        React.createElement("div", { className: "starter-cards-grid" },
                          React.createElement("div", {
                            className: "starter-card",
                            onClick: () => send("Inspect this workspace and explain the structure, architecture, and current git changes.")
                          },
                            React.createElement("div", { className: "starter-header" },
                              React.createElement(Icon.Folder), "Inspect Workspace"),
                            React.createElement("div", { className: "starter-desc" },
                              "Analyze directory tree, git status, and core architecture.")
                          ),
                          React.createElement("div", {
                            className: "starter-card",
                            onClick: () => send("Run the self-test suite and verify that read_file, write_file, edit_file, and run_command are working.")
                          },
                            React.createElement("div", { className: "starter-header" },
                              React.createElement(Icon.Terminal), "Verify Workspace Tools"),
                            React.createElement("div", { className: "starter-desc" },
                              "Test file operations and command execution in-process.")
                          ),
                          React.createElement("div", {
                            className: "starter-card",
                            onClick: () => send("Scan the project routes and minimal API endpoints in this app.")
                          },
                            React.createElement("div", { className: "starter-header" },
                              React.createElement(Icon.File), "Scan API Routes"),
                            React.createElement("div", { className: "starter-desc" },
                              "Map out all endpoints, SSE channels, and WebSocket handlers.")
                          ),
                          React.createElement("div", {
                            className: "starter-card",
                            onClick: () => send("What are the best practices for writing Native AOT safe C# 13 endpoints in Reptar?")
                          },
                            React.createElement("div", { className: "starter-header" },
                              React.createElement(Icon.Edit), "Reptar Best Practices"),
                            React.createElement("div", { className: "starter-desc" },
                              "Zero reflection, typed column tokens, and NetWasm targets.")
                          )
                        )
                      )
                    // Message Stream
                    : messages.map((m, i) =>
                        m.role === "user"
                          ? React.createElement("div", { key: i, className: "turn-row turn-user" },
                              React.createElement("div", { className: "user-bubble" }, m.content)
                            )
                          : React.createElement("div", { key: i, className: "turn-row turn-assistant" },
                              React.createElement("div", { className: "assistant-avatar" }, "R"),
                              React.createElement("div", { className: "assistant-body" },
                                // Step Accordion / Trace
                                m.steps && m.steps.length > 0 && React.createElement(TraceAccordion, { steps: m.steps, isStreaming: m.isStreaming }),
                                // Tool Call Cards
                                m.toolCalls && m.toolCalls.map((t, ti) => React.createElement(ToolCard, { key: ti, tool: t })),
                                // Markdown Content
                                React.createElement("div", {
                                  className: "prose",
                                  dangerouslySetInnerHTML: { __html: parseMd(m.content) }
                                }),
                                m.isStreaming && React.createElement("span", { className: "streaming-cursor" })
                              )
                            )
                      ),
                  React.createElement("div", { ref: endRef })
                )
              ),

              // Floating Prompt Dock
              React.createElement("div", { className: "prompt-dock-container" },
                React.createElement("div", { className: "prompt-box" },
                  // Autocomplete popup
                  autocomplete && React.createElement("div", { className: "autocomplete-menu" },
                    autocomplete.items.map((it, idx) => React.createElement("div", {
                      key: idx,
                      className: `autocomplete-item ${idx === autocomplete.selectedIndex ? "selected" : ""}`,
                      onClick: () => applyAutocomplete(it)
                    },
                      React.createElement("span", null, autocomplete.type === "slash" ? it.cmd : "@" + it.path),
                      React.createElement("span", { className: "autocomplete-desc" }, autocomplete.type === "slash" ? it.desc : it.name)
                    ))
                  ),
                  React.createElement("textarea", {
                    ref: textareaRef,
                    className: "prompt-textarea",
                    placeholder: busy ? "Thinking and executing tools…" : "Ask Reptar Agent anything about this workspace… (Type / for commands, @ for files)",
                    value: input,
                    onInput: handleInput,
                    onKeyDown: handleKeyDown,
                    rows: 1
                  }),
                  React.createElement("div", { className: "prompt-toolbar" },
                    React.createElement("div", { className: "toolbar-left" },
                      React.createElement("button", {
                        className: "toolbar-pill",
                        type: "button",
                        onClick: () => {
                          const atItems = files.slice(0, 8);
                          setAutocomplete({ type: "file", items: atItems, selectedIndex: 0, queryLength: 0 });
                        }
                      },
                        React.createElement(Icon.File), "@ Mention File"
                      ),
                      React.createElement("button", {
                        className: "toolbar-pill",
                        type: "button",
                        onClick: () => {
                          setAutocomplete({ type: "slash", items: SLASH_COMMANDS, selectedIndex: 0 });
                        }
                      },
                        React.createElement(Icon.Terminal), "/ Commands"
                      )
                    ),
                    React.createElement("div", { className: "toolbar-right" },
                      React.createElement("span", { className: "prompt-hint" }, "Enter to send · Shift+Enter for new line"),
                      React.createElement("button", {
                        className: "send-btn",
                        // While streaming, the button aborts; typing still sends a steering message.
                        disabled: !busy && !input.trim(),
                        onClick: () => busy ? abortRun() : send(),
                        title: busy ? "Abort the run" : "Send message"
                      }, busy ? React.createElement(Icon.Stop) : React.createElement(Icon.Send))
                    )
                  )
                )
              )
            ),

            // Provider Connection Modal
            modal === "connect" && React.createElement(ModalBackdrop, { onClose: () => setModal(null) },
              React.createElement("div", { className: "modal-window" },
                React.createElement("div", { className: "modal-header" },
                  React.createElement("div", { className: "modal-title" }, "LLM Provider & Credentials"),
                  React.createElement("button", { className: "sidebar-btn", onClick: () => setModal(null) }, "✕")
                ),
                React.createElement("form", { onSubmit: handleConnect, className: "modal-body" },
                  React.createElement("div", { className: "form-group" },
                    React.createElement("label", { className: "form-label" }, "Select Provider"),
                    React.createElement("select", {
                      className: "form-select",
                      value: selectedProvider,
                      onChange: e => setSelectedProvider(e.target.value)
                    },
                      status?.providers?.map(p => React.createElement("option", { key: p.key, value: p.key },
                        `${p.name} (${p.status})`))
                    )
                  ),
                  React.createElement("div", { className: "form-group" },
                    React.createElement("label", { className: "form-label" }, "API Key"),
                    React.createElement("input", {
                      className: "form-input",
                      type: "password",
                      placeholder: "sk-… (saved to ~/.reptar/agent/auth.json with 0600 permissions)",
                      value: apiKeyInput,
                      onChange: e => setApiKeyInput(e.target.value)
                    })
                  ),
                  React.createElement("div", { className: "form-group" },
                    React.createElement("label", { className: "form-label" }, "Base URL Override (Optional)"),
                    React.createElement("input", {
                      className: "form-input",
                      placeholder: "https://api.openai.com/v1",
                      value: baseUrlInput,
                      onChange: e => setBaseUrlInput(e.target.value)
                    })
                  ),
                  React.createElement("div", { className: "form-group" },
                    React.createElement("label", { className: "form-label" }, "Default Model (Optional)"),
                    React.createElement("input", {
                      className: "form-input",
                      placeholder: "gpt-4o-mini, claude-sonnet-4-5, etc.",
                      value: modelInput,
                      onChange: e => setModelInput(e.target.value)
                    })
                  ),
                  connectResult && React.createElement("div", {
                    style: {
                      padding: "10px 14px",
                      borderRadius: "8px",
                      fontSize: "12.5px",
                      background: connectResult.ok && connectResult.tested ? "var(--green-subtle)" : "var(--amber-subtle)",
                      color: connectResult.ok && connectResult.tested ? "var(--green)" : "var(--amber)",
                      border: `1px solid ${connectResult.ok && connectResult.tested ? "var(--green-border)" : "var(--amber)"}`
                    }
                  },
                    connectResult.tested
                      ? `✓ Successfully verified connection! ${connectResult.modelsCount} models accessible.`
                      : `Saved credential. Note: ${connectResult.testError || "Offline mode active"}`
                  )
                ),
                React.createElement("div", { className: "modal-footer" },
                  React.createElement("button", {
                    type: "button",
                    className: "btn-secondary",
                    onClick: () => setModal(null)
                  }, "Close"),
                  React.createElement("button", {
                    type: "submit",
                    className: "btn-primary",
                    disabled: connectTesting,
                    onClick: handleConnect
                  }, connectTesting ? "Testing Connection…" : "Test & Save Provider")
                )
              )
            ),

            // Tools Modal
            modal === "tools" && React.createElement(ModalBackdrop, { onClose: () => setModal(null) },
              React.createElement("div", { className: "modal-window" },
                React.createElement("div", { className: "modal-header" },
                  React.createElement("div", { className: "modal-title" }, "Mounted Workspace Tools"),
                  React.createElement("button", { className: "sidebar-btn", onClick: () => setModal(null) }, "✕")
                ),
                React.createElement("div", { className: "modal-body" },
                  tools.map(t => React.createElement("div", {
                    key: t.name,
                    style: {
                      background: "#161928",
                      border: "1px solid var(--card-border)",
                      borderRadius: "8px",
                      padding: "12px",
                      display: "flex",
                      flexDirection: "column",
                      gap: "4px"
                    }
                  },
                    React.createElement("div", { style: { display: "flex", alignItems: "center", gap: "8px" } },
                      React.createElement(Icon.Terminal),
                      React.createElement("strong", { style: { fontFamily: "var(--font-mono)", color: "var(--text)" } }, t.name),
                      React.createElement("span", { style: { fontSize: "11px", color: "var(--accent)", background: "var(--accent-subtle)", padding: "1px 6px", borderRadius: "4px" } }, t.method || "Tool")
                    ),
                    React.createElement("div", { style: { fontSize: "12.5px", color: "var(--text-muted)" } }, t.description)
                  ))
                ),
                React.createElement("div", { className: "modal-footer" },
                  React.createElement("button", { className: "btn-primary", onClick: () => setModal(null) }, "Done")
                )
              )
            ),

            // Rename Session Modal
            modal === "rename" && React.createElement(ModalBackdrop, { onClose: () => setModal(null) },
              React.createElement("div", { className: "modal-window", style: { maxWidth: "420px" } },
                React.createElement("div", { className: "modal-header" },
                  React.createElement("div", { className: "modal-title" }, "Rename Session"),
                  React.createElement("button", { className: "sidebar-btn", onClick: () => setModal(null) }, "✕")
                ),
                React.createElement("div", { className: "modal-body" },
                  React.createElement("div", { className: "form-group" },
                    React.createElement("label", { className: "form-label" }, "Session Title"),
                    React.createElement("input", {
                      className: "form-input",
                      value: renameTitle,
                      autoFocus: true,
                      onChange: e => setRenameTitle(e.target.value),
                      onKeyDown: e => { if (e.key === "Enter") saveRename(); }
                    })
                  )
                ),
                React.createElement("div", { className: "modal-footer" },
                  React.createElement("button", { className: "btn-secondary", onClick: () => setModal(null) }, "Cancel"),
                  React.createElement("button", { className: "btn-primary", onClick: saveRename }, "Save")
                )
              )
            )
          );
        }

        // Trace / Reasoning Step Component
        function TraceAccordion({ steps, isStreaming }) {
          const [open, setOpen] = useState(false);
          return React.createElement("div", { className: "trace-accordion" },
            React.createElement("div", { className: "trace-summary", onClick: () => setOpen(!open) },
              React.createElement("div", { className: "trace-title" },
                isStreaming
                  ? React.createElement("div", { className: "trace-spinner" })
                  : React.createElement("span", { style: { color: "var(--green)" } }, "✓"),
                React.createElement("span", null, isStreaming ? "Reasoning & executing steps…" : `Reasoning completed (${steps.length} steps)`)
              ),
              React.createElement(open ? Icon.ChevronDown : Icon.ChevronRight)
            ),
            open && React.createElement("div", { className: "trace-content" },
              steps.map((st, i) => React.createElement("div", { key: i }, `• ${st}`))
            )
          );
        }

        // Tool Execution Card Component
        function ToolCard({ tool }) {
          const [expanded, setExpanded] = useState(false);
          const icon = tool.name === "run_command" ? Icon.Terminal
                     : tool.name === "read_file" ? Icon.File
                     : tool.name === "write_file" ? Icon.Edit
                     : tool.name === "list_dir" ? Icon.Folder
                     : Icon.Terminal;

          return React.createElement("div", { className: `tool-card ${tool.state || "running"}` },
            React.createElement("div", { className: "tool-header", onClick: () => setExpanded(!expanded) },
              React.createElement("div", { className: "tool-identity" },
                React.createElement("div", { className: "tool-icon-badge" }, React.createElement(icon)),
                React.createElement("span", { className: "tool-name" }, tool.name),
                tool.args && React.createElement("span", { className: "tool-args-preview" },
                  typeof tool.args === "string" ? tool.args : JSON.stringify(tool.args))
              ),
              React.createElement("div", { style: { display: "flex", alignItems: "center", gap: "8px" } },
                React.createElement("div", { className: `tool-badge ${tool.state || "running"}` },
                  tool.state === "ok" ? "✓ Done" : tool.state === "bad" ? "✗ Failed" : "Running…",
                  tool.ms ? ` · ${tool.ms}ms` : ""
                ),
                React.createElement(expanded ? Icon.ChevronDown : Icon.ChevronRight)
              )
            ),
            expanded && React.createElement("div", { className: "tool-body" },
              tool.args && React.createElement(React.Fragment, null,
                React.createElement("div", { className: "tool-section-label" }, "Arguments"),
                React.createElement("pre", { className: "tool-code-box" },
                  typeof tool.args === "string" ? tool.args : JSON.stringify(tool.args, null, 2))
              ),
              tool.result && React.createElement(React.Fragment, null,
                React.createElement("div", { className: "tool-section-label" }, "Output"),
                React.createElement("pre", { className: "tool-code-box" }, tool.result)
              )
            )
          );
        }

        function ModalBackdrop({ children, onClose }) {
          return React.createElement("div", {
            className: "modal-backdrop",
            onClick: (e) => { if (e.target === e.currentTarget) onClose(); }
          }, children);
        }

        createRoot(document.getElementById("root")).render(React.createElement(App));
        </script>
        </body>
        </html>
        """;
}
