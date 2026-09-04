#!/usr/bin/env bash
# Генерирует .env с конфигурацией Telegram-бота для docker compose.
# Использование: ./tools/generate-env.sh <BOT_TOKEN> <CHAT_ID>
# Пример:        ./tools/generate-env.sh "123456:ABC-DEF..." 115004568

set -euo pipefail

if [[ $# -ne 2 ]]; then
    echo "Использование: $0 <BOT_TOKEN> <CHAT_ID>" >&2
    exit 1
fi

TOKEN="$1"
CHAT_ID="$2"

if [[ ! "$TOKEN" =~ ^[0-9]+:[A-Za-z0-9_-]+$ ]]; then
    echo "Ошибка: токен не похож на формат Telegram (\"<id>:<hash>\")" >&2
    exit 1
fi

if [[ ! "$CHAT_ID" =~ ^-?[0-9]+$ ]]; then
    echo "Ошибка: chat id должен быть числом" >&2
    exit 1
fi

if [[ -f .env ]]; then
    echo "Файл .env уже существует — перезапись не выполнена." >&2
    echo "Обновите его вручную или удалите и запустите скрипт заново." >&2
    exit 1
fi

cat > .env <<EOF
# Сгенерировано tools/generate-env.sh — не коммитить (уже в .gitignore).
# Формат: переменные окружения перекрывают appsettings.json (разделитель __).

Telegram__BotToken=${TOKEN}
# Несколько чатов: Telegram__AllowedChatIds__0, __1, __2, ...
Telegram__AllowedChatIds__0=${CHAT_ID}

# Tcp__Port=5023
# ASPNETCORE_ENVIRONMENT=Production
EOF

chmod 600 .env
echo "Создан .env (права 600). Запуск: docker compose up -d --build"
