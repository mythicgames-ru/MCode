#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""
change.py — Celar Engine, итерация 11: bugfix 'Used' без имени тула.

ПРИЧИНА: ProcessOpenAIChunk затирал непустое name пустым, если провайдер
присылал 'name: ""' в одном из последующих чанков. start-событие успевало
уйти с валидным именем, а done-событие — уже с пустым. UI показывал
'Used ' (без имени), хотя модель и executor реально работали (модель
перезапрашивала тул, получая 'unknown tool: ' на первой попытке).

ФИКСЫ:
  1) ChatService.cs → ProcessOpenAIChunk: не затирать непустое name пустым.
  2) Chat.html:
        – toolTitle() для пустого/неизвестного имени показывает
          "Working" (running) / "Done" (finished) вместо "Used " с хвостом;
        – showToolDone() берёт line.dataset.tool как fallback, если
          пришёл пустой tool;
        – showToolStart() логирует warn, если его вызвали с пустым
          именем (для диагностики в dotnet run).

Все патчи точечные, идемпотентные. Трогает только 2 файла.
"""

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
print("=" * 64)
print(" Celar Engine — итерация 11: fix 'Used' without tool name")
print("=" * 64)
print()

patched_any = False


def find_file(candidates, marker=None):
    for c in candidates:
        p = Path(c)
        if not p.is_absolute():
            p = ROOT / p
        if not p.exists():
            continue
        try:
            text = p.read_text(encoding="utf-8")
        except Exception as e:
            print(f"  [!] {p}: read error {e}")
            continue
        if marker is None or marker in text:
            return p, text
    return None, None


def report_missing(label, candidates):
    print(f"  [!] {label}: не найден. Проверены пути:")
    for c in candidates:
        p = Path(c)
        if not p.is_absolute():
            p = ROOT / p
        print(f"       - {p} ({'ok' if p.exists() else 'missing'})")


# ═════════════════════════════════════════════════════════════════
#  1) ChatService.cs — не затирать имя тула пустым
# ═════════════════════════════════════════════════════════════════

print("[1/2] ChatService.cs")

CHAT_CANDIDATES = [
    "Editor/ChatService.cs",
    "ChatService.cs",
    "src/ChatService.cs",
    "AgentApp/Editor/ChatService.cs",
]
CHAT, src = find_file(CHAT_CANDIDATES, marker="ProcessOpenAIChunk")

if CHAT is None:
    report_missing("ChatService.cs", CHAT_CANDIDATES)
else:
    print(f"  [i] найден: {CHAT}")
    orig = src

    OLD_LINE = 'b.Name = nm.GetString() ?? "";'
    # Same-line block: сохраняет отступ, не ломает C#-грамматику,
    # не затирает непустое имя пустым.
    NEW_LINE = '{ var __n = nm.GetString(); if (!string.IsNullOrEmpty(__n)) b.Name = __n; }'

    if 'if (!string.IsNullOrEmpty(__n)) b.Name = __n;' in src:
        print("  [=] name-preservation уже есть")
    elif OLD_LINE in src:
        src = src.replace(OLD_LINE, NEW_LINE, 1)
        print("  [+] ProcessOpenAIChunk: preserve non-empty name")
    else:
        print("  [!] anchor b.Name = nm.GetString() не найден")

    if src != orig:
        CHAT.write_text(src, encoding="utf-8")
        patched_any = True
        print("  [→] сохранён")


# ═════════════════════════════════════════════════════════════════
#  2) Editor/Tools/Chat.html — fallback в отображении
# ═════════════════════════════════════════════════════════════════

print("[2/2] Chat.html (editor tool)")

CHATHTML_CANDIDATES = [
    "Editor/Tools/chat.html",
    "Editor/Tools/Chat.html",
    "Editor/Tools/CHAT.html",
]
CHATHTML, src = find_file(CHATHTML_CANDIDATES, marker="function toolTitle")

if CHATHTML is None:
    report_missing("Chat.html", CHATHTML_CANDIDATES)
else:
    print(f"  [i] найден: {CHATHTML}")
    orig = src

    # ── 2a. robust toolTitle ─────────────────────────────────────
    TT_OLD = '''function toolTitle(tool,running){
  switch(tool){
    case "list_directory":return running?"Reading directory":"Read directory";
    case "read_file":return running?"Reading file":"Read file";
    case "edit_file":return running?"Editing file":"Edited file";
    case "run_command":return running?"Running command":"Ran command";
    case "web_search":return running?"Searching":"Searched";
    case "read_context":return running?"Reading Context":"Read Context";
    case "download_file":return running?"Downloading file":"Downloaded file";
    case "watch":return running?"Viewing image":"Viewed image";
    case "ask_ai":return running?"Asking AI":"Asked AI";
    case "question":return running?"Asking user":"Asked user";
    case "get_time":return running?"Checking time":"Checked time";
    default:return running?("Using "+tool):("Used "+tool);
  }
}'''

    TT_NEW = '''function toolTitle(tool,running){
  const t=String(tool==null?"":tool).trim();
  // fallback: никогда не показываем "Used " с пустым именем
  if(!t) return running?"Working":"Done";
  switch(t){
    case "list_directory":return running?"Reading directory":"Read directory";
    case "read_file":return running?"Reading file":"Read file";
    case "edit_file":return running?"Editing file":"Edited file";
    case "run_command":return running?"Running command":"Ran command";
    case "web_search":return running?"Searching":"Searched";
    case "read_context":return running?"Reading Context":"Read Context";
    case "download_file":return running?"Downloading file":"Downloaded file";
    case "watch":return running?"Viewing image":"Viewed image";
    case "ask_ai":return running?"Asking AI":"Asked AI";
    case "question":return running?"Asking user":"Asked user";
    case "get_time":return running?"Checking time":"Checked time";
    default:return running?("Using "+t):("Used "+t);
  }
}'''

    if '// fallback: никогда не показываем "Used " с пустым именем' in src:
        print("  [=] toolTitle уже robust")
    elif TT_OLD in src:
        src = src.replace(TT_OLD, TT_NEW, 1)
        print("  [+] toolTitle: fallback для пустого имени")
    else:
        print("  [!] anchor toolTitle не совпал — версия файла отличается")

    # ── 2b. showToolDone: fallback на line.dataset.tool ──────────
    SD_OLD = '''function showToolDone(tool,added,removed,extra){
  if(!currentToolLine){
    // done без активного старта — второй параллельный вызов уже завершённого тула.
    // Новую строку создавать не надо — она бы повисла в состоянии "done" без старта.
    return;
  }
  const line=currentToolLine;currentToolLine=null;
  line.classList.remove("shimmer");
  const tt=line.querySelector(".tool-text");
  if(tt){
    if(tool==="web_search"){
      const n=added|0;
      tt.textContent="Searched "+(n===1?"1 page":n+" pages");
    }else{
      tt.textContent=toolTitle(tool,false);
    }
  }'''

    SD_NEW = '''function showToolDone(tool,added,removed,extra){
  if(!currentToolLine){
    // done без активного старта — второй параллельный вызов уже завершённого тула.
    // Новую строку создавать не надо — она бы повисла в состоянии "done" без старта.
    return;
  }
  const line=currentToolLine;currentToolLine=null;
  line.classList.remove("shimmer");
  // защита: если пришёл пустой tool, восстановим имя из dataset (start-события)
  const effTool=String(tool||line.dataset.tool||"").trim();
  const tt=line.querySelector(".tool-text");
  if(tt){
    if(effTool==="web_search"){
      const n=added|0;
      tt.textContent="Searched "+(n===1?"1 page":n+" pages");
    }else{
      tt.textContent=toolTitle(effTool,false);
    }
  }'''

    if 'const effTool=String(tool||line.dataset.tool||"")' in src:
        print("  [=] showToolDone уже с fallback")
    elif SD_OLD in src:
        src = src.replace(SD_OLD, SD_NEW, 1)
        print("  [+] showToolDone: fallback на line.dataset.tool")
    else:
        print("  [!] anchor showToolDone не совпал")

    # ── 2c. warn в showToolStart для диагностики ─────────────────
    SS_OLD = """function showToolStart(bodyEl,tool){
  finalizeCurrentToolLine();"""
    SS_NEW = """function showToolStart(bodyEl,tool){
  try{
    const __t=String(tool==null?"":tool).trim();
    if(!__t) console.warn("[chat] showToolStart got empty tool name");
  }catch(_){}
  finalizeCurrentToolLine();"""

    if '[chat] showToolStart got empty tool name' in src:
        print("  [=] showToolStart уже логирует")
    elif SS_OLD in src:
        src = src.replace(SS_OLD, SS_NEW, 1)
        print("  [+] showToolStart: warn для пустого tool")
    else:
        print("  [!] anchor showToolStart не совпал")

    if src != orig:
        CHATHTML.write_text(src, encoding="utf-8")
        patched_any = True
        print("  [→] сохранён")


print()
print("=" * 64)
if patched_any:
    print(" Готово. Дальше:")
    print()
    print("   dotnet build")
    print("   dotnet run")
    print()
    print(" Что фиксится:")
    print("  • ChatService.cs: имя тула больше не затирается пустым.")
    print("    Это и был главный баг — провайдер слал 'name:\"\"' в одном")
    print("    из чанков, onToolStart успевал поймать валидное имя,")
    print("    а done видел уже пустую строку → UI рисовал 'Used '.")
    print()
    print("  • Chat.html: если пустое имя всё-таки проскочит:")
    print("      – в шапке будет 'Working' / 'Done' вместо 'Used ';")
    print("      – showToolDone попытается восстановить имя из dataset;")
    print("      – warn в devtools, если start пришёл с пустым tool.")
    print()
    print(" После фикса AI будет сразу получать рабочий тул с первой")
    print(" попытки — без retry'ев на 'unknown tool: '.")
else:
    print(" Нечего делать — всё уже на месте.")
print("=" * 64)

sys.exit(0)