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
        <style>
          :root {
            --bg: #0b0e14; --panel: #11151f; --line: #1d2330; --text: #dbe2f0;
            --dim: #7d8aa0; --accent: #7c6cf2; --green: #3ecf8e; --red: #ff6b6b;
            --yellow: #ffc86b; --mono: ui-monospace, "SF Mono", Menlo, Consolas, monospace;
          }
          * { box-sizing: border-box; }
          body { margin: 0; background: var(--bg); color: var(--text);
                 font: 14px/1.5 -apple-system, "Segoe UI", Roboto, sans-serif; height: 100vh; display: flex; }
          aside { width: 240px; border-right: 1px solid var(--line); padding: 16px; overflow-y: auto; }
          main { flex: 1; display: flex; flex-direction: column; min-width: 0; }
          header { padding: 14px 20px; border-bottom: 1px solid var(--line);
                   display: flex; align-items: center; gap: 10px; }
          .logo { font-weight: 700; letter-spacing: .06em; }
          .logo b { color: var(--accent); }
          .model { color: var(--dim); font-family: var(--mono); font-size: 12px; }
          #messages { flex: 1; overflow-y: auto; padding: 20px; display: flex;
                      flex-direction: column; gap: 16px; }
          .msg { max-width: 860px; white-space: pre-wrap; word-wrap: break-word; }
          .msg.user { align-self: flex-end; background: var(--accent); color: white;
                      padding: 10px 14px; border-radius: 14px 14px 4px 14px; max-width: 70%; }
          .msg.assistant { align-self: flex-start; }
          .msg.assistant h1, .msg.assistant h2, .msg.assistant h3 { margin: .6em 0 .3em; }
          .msg code { font-family: var(--mono); background: #1a2030; padding: 1px 5px;
                      border-radius: 4px; font-size: 12.5px; }
          .msg pre { background: #0d1117; border: 1px solid var(--line); padding: 12px;
                     border-radius: 8px; overflow-x: auto; font-family: var(--mono); font-size: 12.5px; }
          .tool { align-self: flex-start; color: var(--dim); font-family: var(--mono);
                  font-size: 12px; padding: 3px 10px; border: 1px solid var(--line);
                  border-radius: 12px; background: var(--panel); }
          .tool.ok { color: var(--green); border-color: #234a38; }
          .tool.bad { color: var(--red); border-color: #4a2323; }
          form { border-top: 1px solid var(--line); padding: 14px 20px; display: flex; gap: 10px; }
          textarea { flex: 1; background: var(--panel); color: var(--text); border: 1px solid var(--line);
                     border-radius: 10px; padding: 10px 12px; font: inherit; resize: none; height: 46px; }
          button { background: var(--accent); color: white; border: 0; border-radius: 10px;
                   padding: 0 20px; font: inherit; font-weight: 600; cursor: pointer; }
          button:disabled { opacity: .5; cursor: default; }
          .session { padding: 8px 10px; border-radius: 8px; cursor: pointer; color: var(--dim);
                     overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
          .session:hover, .session.active { background: var(--panel); color: var(--text); }
          .newchat { width: 100%; margin-bottom: 12px; background: var(--panel); color: var(--text);
                     border: 1px solid var(--line); padding: 8px; border-radius: 8px; }
          aside h2 { font-size: 11px; text-transform: uppercase; letter-spacing: .12em;
                     color: var(--dim); margin: 16px 0 8px; }
          .empty { color: var(--dim); margin: auto; text-align: center; }
          .empty b { color: var(--text); }
          @media (max-width: 760px) { aside { display: none; } }
        </style>
        </head>
        <body>
        <div id="root"><div class="empty">Loading Reptar Agent…</div></div>
        <script type="module">
        import React from "https://esm.sh/react@19";
        import { createRoot } from "https://esm.sh/react-dom@19/client";

        const md = (text) => {
          const esc = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;");
          const lines = esc(text).split("\\n");
          let html = "", inCode = false, code = [];
          for (const line of lines) {
            if (line.trim().startsWith("```")) {
              if (inCode) { html += "<pre><code>" + code.join("\\n") + "</code></pre>"; code = []; }
              inCode = !inCode; continue;
            }
            if (inCode) { code.push(line); continue; }
            let out = line
              .replace(/`([^`]+)`/g, "<code>$1</code>")
              .replace(/\\*\\*([^*]+)\\*\\*/g, "<strong>$1</strong>");
            if (/^###\\s/.test(out)) out = "<h3>" + out.slice(4) + "</h3>";
            else if (/^##\\s/.test(out)) out = "<h2>" + out.slice(3) + "</h2>";
            else if (/^#\\s/.test(out)) out = "<h1>" + out.slice(2) + "</h1>";
            else if (/^[-*]\\s/.test(out)) out = "<li>" + out.slice(2) + "</li>";
            html += out + "<br/>";
          }
          if (code.length) html += "<pre><code>" + code.join("\\n") + "</code></pre>";
          return html;
        };

        function App() {
          const [messages, setMessages] = React.useState([]);
          const [tools, setTools] = React.useState([]);
          const [sessions, setSessions] = React.useState([]);
          const [sessionId, setSessionId] = React.useState(null);
          const [input, setInput] = React.useState("");
          const [busy, setBusy] = React.useState(false);
          const endRef = React.useRef(null);

          const refreshSessions = React.useCallback(async () => {
            const res = await fetch("/api/sessions");
            if (res.ok) setSessions(await res.json());
          }, []);

          const loadTools = React.useCallback(async () => {
            const res = await fetch("/api/tools");
            if (res.ok) setTools((await res.json()).map((t) => t.name));
          }, []);

          React.useEffect(() => { refreshSessions(); loadTools(); }, []);

          const send = async (event) => {
            event.preventDefault();
            const text = input.trim();
            if (!text || busy) return;
            setInput(""); setBusy(true);
            setMessages((m) => [...m, { role: "user", content: text }]);

            const res = await fetch("/api/chat", {
              method: "POST",
              headers: { "content-type": "application/json" },
              body: JSON.stringify({ message: text, sessionId }),
            });

            if (!res.ok || !res.body) {
              setMessages((m) => [...m, { role: "assistant", content: "error: " + res.status }]);
              setBusy(false); return;
            }

            const reader = res.body.getReader();
            const decoder = new TextDecoder();
            let buffer = "", assistant = "";

            const flush = (frame) => {
              if (!frame || frame === "[DONE]") return;
              let evt; try { evt = JSON.parse(frame); } catch { return; }
              if (evt.type === "text") { assistant += evt.text; paint(); }
              else if (evt.type === "tool_start") {
                setMessages((m) => [...m, { role: "tool", name: evt.name, state: "start" }]);
              } else if (evt.type === "tool_end") {
                setMessages((m) => [...m, { role: "tool", name: evt.name, state: evt.success ? "ok" : "bad", ms: evt.elapsedMs }]);
              } else if (evt.type === "done") {
                if (evt.sessionId) setSessionId(evt.sessionId);
                refreshSessions();
              } else if (evt.type === "error") {
                assistant += "\\n\\n**error:** " + evt.message; paint();
              }
            };

            let painted = false;
            const paint = () => {
              if (painted) {
                setMessages((m) => {
                  const copy = [...m];
                  copy[copy.length - 1] = { role: "assistant", content: assistant };
                  return copy;
                });
              } else {
                painted = true;
                setMessages((m) => [...m, { role: "assistant", content: assistant }]);
              }
            };

            while (true) {
              const { done, value } = await reader.read();
              if (done) break;
              buffer += decoder.decode(value, { stream: true });
              let index;
              while ((index = buffer.indexOf("\\n\\n")) >= 0) {
                const chunk = buffer.slice(0, index);
                buffer = buffer.slice(index + 2);
                if (chunk.startsWith("data:")) flush(chunk.slice(5).trim());
              }
            }
            setBusy(false);
          };

          const openSession = async (id) => {
            const res = await fetch("/api/sessions/" + id + "/messages");
            if (!res.ok) return;
            const rows = await res.json();
            setSessionId(id);
            setMessages(rows.map((r) => ({
              role: r.role === "tool" ? "tool" : r.role,
              content: r.content, name: r.toolCalls?.[0]?.name,
            })));
          };

          React.useEffect(() => { endRef.current?.scrollIntoView({ behavior: "smooth" }); }, [messages]);

          return (
            React.createElement(React.Fragment, null,
              React.createElement("aside", null,
                React.createElement("h2", null, "Reptar Agent"),
                React.createElement("button", { className: "newchat", onClick: () => { setSessionId(null); setMessages([]); } }, "+ New chat"),
                React.createElement("h2", null, "Sessions"),
                sessions.map((s) =>
                  React.createElement("div", {
                    key: s.id,
                    className: "session" + (s.id === sessionId ? " active" : ""),
                    onClick: () => openSession(s.id),
                  }, s.title || s.id)),
                React.createElement("h2", null, "Tools"),
                tools.map((t) => React.createElement("div", { key: t, className: "session" }, t)),
              ),
              React.createElement("main", null,
                React.createElement("header", null,
                  React.createElement("div", { className: "logo" }, React.createElement("b", null, "R"), "EPTAR AGENT"),
                  React.createElement("span", { className: "model" },
                    sessionId ? "session " + sessionId.slice(0, 12) + "…" : "new session"),
                ),
                React.createElement("div", { id: "messages" },
                  messages.length === 0
                    ? React.createElement("div", { className: "empty" },
                        React.createElement("b", null, "Reptar Agent web companion"),
                        React.createElement("div", null, "Ask the agent to inspect, edit, or run your workspace."),
                        React.createElement("div", null, "POST /api/chat streams SSE · tools dispatch in-process"))
                    : messages.map((m, i) =>
                        m.role === "tool"
                          ? React.createElement("div", {
                              key: i,
                              className: "tool " + (m.state || ""),
                            }, (m.state === "ok" ? "✓ " : m.state === "bad" ? "✗ " : "→ ") + m.name +
                               (m.ms ? " · " + m.ms + "ms" : ""))
                          : React.createElement("div", {
                              key: i,
                              className: "msg " + m.role,
                              dangerouslySetInnerHTML: { __html: md(m.content) },
                            })),
                  React.createElement("div", { ref: endRef }),
                ),
                React.createElement("form", { onSubmit: send },
                  React.createElement("textarea", {
                    value: input,
                    placeholder: busy ? "thinking…" : "Ask the agent anything about this workspace…",
                    onChange: (e) => setInput(e.target.value),
                    onKeyDown: (e) => {
                      if (e.key === "Enter" && !e.shiftKey) send(e);
                    },
                  }),
                  React.createElement("button", { disabled: busy }, "Send"),
                ),
              ),
            )
          );
        }

        createRoot(document.getElementById("root")).render(React.createElement(App));
        </script>
        </body>
        </html>
        """;
}
