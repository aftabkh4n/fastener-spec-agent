const chatEl = document.getElementById("chat");
const form = document.getElementById("composer");
const input = document.getElementById("input");
const sendBtn = document.getElementById("send");

const history = [];

if (window.marked?.setOptions) {
  window.marked.setOptions({ gfm: true, breaks: false });
}

function renderMarkdown(md) {
  if (!window.marked || !window.DOMPurify) return md;
  const raw = window.marked.parse(md ?? "");
  return window.DOMPurify.sanitize(raw);
}

function el(tag, className, text) {
  const e = document.createElement(tag);
  if (className) e.className = className;
  if (text !== undefined) e.textContent = text;
  return e;
}

function renderUser(text) {
  const wrap = el("div", "msg user");
  wrap.textContent = text;
  chatEl.appendChild(wrap);
  wrap.scrollIntoView({ behavior: "smooth", block: "end" });
}

function createAssistantSlot() {
  const wrap = el("div", "msg assistant");
  const body = el("div", "body", "");
  wrap.appendChild(body);
  chatEl.appendChild(wrap);
  wrap.scrollIntoView({ behavior: "smooth", block: "end" });
  return { wrap, body };
}

function updateToolCalls(wrap, toolCalls) {
  let details = wrap.querySelector("details.tools");
  if (!details) {
    details = document.createElement("details");
    details.className = "tools";
    details.appendChild(el("summary"));
    details.appendChild(document.createElement("ol"));
    wrap.appendChild(details);
  }
  details.querySelector("summary").textContent =
    `Looked up ${toolCalls.length} source${toolCalls.length > 1 ? "s" : ""}`;
  const list = details.querySelector("ol");
  list.innerHTML = "";
  for (const t of toolCalls) {
    const li = document.createElement("li");
    li.appendChild(el("code", null, t.name));
    if (t.args) {
      li.appendChild(document.createTextNode(" "));
      li.appendChild(el("span", "args", t.args));
    }
    list.appendChild(li);
  }
}

async function send(question) {
  input.value = "";
  sendBtn.disabled = true;

  renderUser(question);
  history.push({ role: "user", content: question });

  const { wrap, body } = createAssistantSlot();
  body.textContent = "Thinking…";
  body.classList.add("thinking-inline");

  const toolCalls = [];
  let full = "";
  let started = false;

  try {
    const res = await fetch("/api/chat", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Accept: "text/event-stream"
      },
      body: JSON.stringify({ messages: history })
    });

    if (!res.ok) {
      const text = await res.text();
      wrap.classList.add("error");
      body.textContent = `Request failed (${res.status}): ${text}`;
      return;
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";

    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true });

      let idx;
      while ((idx = buffer.indexOf("\n\n")) !== -1) {
        const raw = buffer.slice(0, idx);
        buffer = buffer.slice(idx + 2);
        if (!raw.trim()) continue;

        let ev = "message";
        let data = "";
        for (const line of raw.split("\n")) {
          if (line.startsWith("event: ")) ev = line.slice(7);
          else if (line.startsWith("data: ")) data += line.slice(6);
        }
        if (!data) continue;
        let payload;
        try { payload = JSON.parse(data); } catch { continue; }

        if (ev === "text") {
          if (!started) {
            started = true;
            body.textContent = "";
            body.classList.remove("thinking-inline");
            body.classList.add("markdown");
          }
          full += payload.delta || "";
          body.innerHTML = renderMarkdown(full);
          wrap.scrollIntoView({ block: "end" });
        } else if (ev === "tool") {
          toolCalls.push(payload);
          updateToolCalls(wrap, toolCalls);
          if (!started) body.textContent = "Looking up sources…";
        } else if (ev === "error") {
          wrap.classList.add("error");
          body.textContent = `Error: ${payload.message}`;
        } else if (ev === "done") {
          // final flush
        }
      }
    }

    if (full) history.push({ role: "assistant", content: full });
  } catch (err) {
    wrap.classList.add("error");
    body.textContent = `Network error: ${err.message}`;
  } finally {
    sendBtn.disabled = false;
    input.focus();
  }
}

form.addEventListener("submit", (e) => {
  e.preventDefault();
  const q = input.value.trim();
  if (!q) return;
  send(q);
});

input.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey) {
    e.preventDefault();
    form.requestSubmit();
  }
});

document.querySelectorAll(".chip").forEach((btn) => {
  btn.addEventListener("click", () => {
    const q = btn.getAttribute("data-q");
    if (q) send(q);
  });
});
