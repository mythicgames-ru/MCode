#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
change.py — фикс ошибки:
    ⚠️ Cannot access a disposed object.
    Object name: 'JsonDocument'.

Причина:
  В Editor.cs.OnWebMessage есть `using var doc = JsonDocument.Parse(message)`,
  который освобождает JsonDocument при выходе из метода. Для action=="chat-send"
  вызывается fire-and-forget `_ = HandleChatSendAsync(r)` — async-метод
  возвращается после первого `await` (InitializeChatAsync), но к тому моменту
  OnWebMessage уже завершился, JsonDocument мёртв, и любое обращение к
  `root.TryGetProperty(...)` внутри HandleChatSendAsync валится с
  ObjectDisposedException.

Фикс:
  Передаём в HandleChatSendAsync клон JsonElement — `r.Clone()` — он владеет
  собственной памятью и живёт сколько нужно, независимо от исходного
  JsonDocument.

Идемпотентно. Ничего больше не трогает.
"""

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
SKIP = {"bin", "obj", ".git", ".vs", "node_modules", ".idea"}


def find_one(name: str) -> Path | None:
    for p in ROOT.rglob(name):
        if any(part in SKIP for part in p.parts):
            continue
        return p
    return None


# ===========================================================================
#  Editor.cs — единственное место, где нужен фикс
# ===========================================================================
print("[1/1] Editor.cs")
ED = find_one("Editor.cs")
if not ED:
    print("  [!] Editor.cs не найден"); sys.exit(1)

text = ED.read_text(encoding="utf-8")
changed = False

# Уже пропатчено?
already_variants = (
    'HandleChatSendAsync(r.Clone())',
    'HandleChatSendAsync(root.Clone())',
)

# Варианты исходной записи — с одним пробелом/без, с переносами и т.п.
variants = [
    (
        'case "chat-send": _ = HandleChatSendAsync(r); break;',
        'case "chat-send": _ = HandleChatSendAsync(r.Clone()); break;',
    ),
    (
        'case "chat-send":_ = HandleChatSendAsync(r); break;',
        'case "chat-send":_ = HandleChatSendAsync(r.Clone()); break;',
    ),
    (
        'case "chat-send": _=HandleChatSendAsync(r); break;',
        'case "chat-send": _=HandleChatSendAsync(r.Clone()); break;',
    ),
]

if any(v in text for v in already_variants):
    print("  [=] фикс уже применён")
else:
    done = False
    for old, new in variants:
        if old in text:
            text = text.replace(old, new, 1)
            print(f"  [+] {old.strip()!r}")
            print(f"      -> {new.strip()!r}")
            changed = True
            done = True
            break

    if not done:
        # Фоллбэк: многострочная запись switch-case (как в отступах репо)
        old_multi = (
            'case "chat-send":\n'
            '                _ = HandleChatSendAsync(r);\n'
            '                break;'
        )
        new_multi = (
            'case "chat-send":\n'
            '                _ = HandleChatSendAsync(r.Clone());\n'
            '                break;'
        )
        if old_multi in text:
            text = text.replace(old_multi, new_multi, 1)
            print("  [+] многострочный case chat-send -> r.Clone()")
            changed = True
        else:
            # Совсем последний фоллбэк: найти `HandleChatSendAsync(r)` в любом контексте
            needle = 'HandleChatSendAsync(r)'
            if needle in text:
                text = text.replace(needle, 'HandleChatSendAsync(r.Clone())', 1)
                print(f"  [+] заменил {needle!r} -> 'HandleChatSendAsync(r.Clone())'")
                changed = True
            else:
                print("  [!] не нашёл вызов HandleChatSendAsync(r) — файл уже другой?")
                print("      ожидалась строка: case \"chat-send\": _ = HandleChatSendAsync(r); break;")

if changed:
    ED.write_text(text, encoding="utf-8")
    print(f"  → {ED.relative_to(ROOT)}")
else:
    print("  [=] Editor.cs без изменений")


print()
print("Готово. Пересобери: dotnet run")
print()
print("Что произошло под капотом:")
print("  • Причина ошибки: JsonDocument освобождался раньше, чем async-задача")
print("    HandleChatSendAsync успевала обратиться к своему JsonElement.")
print("  • Фикс: r.Clone() создаёт независимую копию, которая живёт после")
print("    возврата из OnWebMessage. Теперь первый запрос чата (когда")
print("    _chatRegistered ещё false и есть await) не падает.")
print()
print("Проверка: отправь первое сообщение сразу после открытия проекта —")
print("должно уйти на сервер без 'Cannot access a disposed object'.")