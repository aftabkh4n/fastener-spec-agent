const chatEl = document.getElementById("chat");
const form = document.getElementById("composer");
const input = document.getElementById("input");
const sendBtn = document.getElementById("send");

const history = [];

function el(tag, className, text) {
  const e = document.createElement(tag);
  if (className) e.className = className;
  if (text !== undefined) e.textContent = text;
  return e;
}

function renderMessage(role, text, toolCalls) {
  const wrap = el("div", `msg ${role}`);
  wrap.appendChild(el("div", "body", text));

  if (toolCalls && toolCalls.length) {
    const details = document.createElement("details");
    details.className = "tools";
    const summary = document.createElement("summary");
    summary.textContent = `Looked up ${toolCalls.length} source${toolCalls.length > 1 ? "s" : ""}`;
    details.appendChild(summary);
    const list = document.createElement("ol");
    for (const t of toolCalls) {
      const li = document.createElement("li");
      const code = el("code", null, t.name);
      li.appendChild(code);
      if (t.args) {
        li.appendChild(document.createTextNode(" "));
        const small = el("span", "args", t.args);
        li.appendChild(small);
      }
      list.appendChild(li);
    }
    details.appendChild(list);
    wrap.appendChild(details);
  }

  chatEl.appendChild(wrap);
  wrap.scrollIntoView({ behavior: "smooth", block: "end" });
  return wrap;
}

async function send(question) {
  input.value = "";
  sendBtn.disabled = true;

  renderMessage("user", question);
  history.push({ role: "user", content: question });

  const thinking = renderMessage("assistant thinking", "Thinking…");

  try {
    const res = await fetch("/api/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ messages: history })
    });

    if (!res.ok) {
      const text = await res.text();
      thinking.remove();
      renderMessage("error", `Request failed (${res.status}): ${text}`);
      return;
    }

    const data = await res.json();
    thinking.remove();
    renderMessage("assistant", data.answer || "(empty response)", data.toolCalls || []);
    history.push({ role: "assistant", content: data.answer || "" });
  } catch (err) {
    thinking.remove();
    renderMessage("error", `Network error: ${err.message}`);
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
