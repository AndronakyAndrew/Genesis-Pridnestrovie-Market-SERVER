#!/usr/bin/env bash
# ============================================================================
#  Genesis Market — чтение .env обслуживающими скриптами.
#
#  Почему не `set -a; source .env`: значения там — обычный текст, а не код
#  shell. `SMTP_FROM_NAME=Местная площадка Genesis` при source разбирается как
#  присваивание «Местная» плюс запуск команды «площадка», и под `set -e` скрипт
#  умирает на месте (так с 2026-09-07, с коммита ребрендинга, падал ночной
#  backup.sh). А `RESEND_FROM_EMAIL=... <noreply@...>` — ещё и перенаправление
#  ввода. Плюс source исполняет что угодно, что окажется в .env.
#
#  Здесь значение берётся дословно — всё после первого «=», без интерпретации.
#
#  Уже заданные снаружи переменные приоритетнее .env: так скрипт можно нацелить
#  на другой стек, не редактируя продовый файл:
#    POSTGRES_DB=genesis_check ./scripts/restore-check.sh
#
#  Подключение (SCRIPT_DIR вычисляется до cd, см. вызывающие скрипты):
#    source "$SCRIPT_DIR/load-env.sh"; load_env .env
# ============================================================================

load_env() {
  local file="${1:-.env}"
  [[ -f "$file" ]] || return 0

  local line key value
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"                       # .env, сохранённый в Windows
    [[ "$line" =~ ^[[:space:]]*(#|$) ]] && continue
    [[ "$line" == *=* ]] || continue

    key="${line%%=*}"
    key="${key//[[:space:]]/}"
    [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
    [[ -n "${!key-}" ]] && continue            # снаружи задано — не перетираем

    value="${line#*=}"
    # Парную внешнюю кавычку снимаем: Compose читает тот же файл и снимает её,
    # иначе один .env означал бы у Compose и у скриптов разное.
    if (( ${#value} >= 2 )); then
      if [[ "$value" == \"*\" ]]; then value="${value:1:${#value}-2}"
      elif [[ "$value" == \'*\' ]]; then value="${value:1:${#value}-2}"
      fi
    fi

    export "$key=$value"
  done < "$file"
}
