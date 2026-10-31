const DEFAULTS = {
  endpoint: "http://127.0.0.1:17853/v1/translate",
  token: "",
  target: "简体中文",
};

const fields = {
  endpoint: document.getElementById("endpoint"),
  token: document.getElementById("token"),
  target: document.getElementById("target"),
};
const status = document.getElementById("status");

chrome.storage.local.get(DEFAULTS).then((config) => {
  for (const [key, input] of Object.entries(fields)) {
    input.value = config[key] ?? "";
  }
});

document.getElementById("save").addEventListener("click", async () => {
  const config = {};
  for (const [key, input] of Object.entries(fields)) {
    const value = input.value.trim();
    config[key] = value || DEFAULTS[key];
  }

  await chrome.storage.local.set(config);
  status.textContent = "已保存。";
  setTimeout(() => { status.textContent = ""; }, 2000);
});
