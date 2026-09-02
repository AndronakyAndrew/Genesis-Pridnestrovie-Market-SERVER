# Удобные команды для dev-окружения Genesis Market (docker-compose.dev.yml).
# Полностью изолировано от prod — см. README-dev.md.
#
# Windows: нужен GNU make (Git Bash сам по себе make не ставит) —
# `choco install make` либо запускай команды из COMPOSE напрямую, без make.

COMPOSE_FILE := docker-compose.dev.yml
ENV_FILE     := .env.dev
COMPOSE      := docker compose -f $(COMPOSE_FILE) --env-file $(ENV_FILE)

.PHONY: help dev-up dev-down dev-logs dev-rebuild dev-reset check-env-dev

help:
	@echo "make dev-up      — поднять dev-стек (api, postgres, minio, adminer)"
	@echo "make dev-down    — остановить, данные в volumes сохраняются"
	@echo "make dev-logs    — логи всех сервисов (Ctrl+C — выход)"
	@echo "make dev-rebuild — пересобрать api с нуля (чистит bin/obj, рестартует контейнер)"
	@echo "make dev-reset   — снести volumes dev (БД, MinIO) и поднять начисто"

check-env-dev:
	@if [ ! -f $(ENV_FILE) ]; then \
		echo "Нет $(ENV_FILE). Скопируй шаблон: cp .env.dev.example .env.dev"; \
		exit 1; \
	fi

dev-up: check-env-dev
	$(COMPOSE) up -d --build

dev-down:
	$(COMPOSE) down

dev-logs:
	$(COMPOSE) logs -f --tail=200

# Чистит закешированные bin/obj (named volumes из docker-compose.dev.yml) и
# пересоздаёт контейнер api — нужно после смены .csproj/добавления пакетов,
# когда dotnet watch сам не подхватывает изменения.
dev-rebuild: check-env-dev
	-$(COMPOSE) exec api sh -c "find /src/src/GenesisMarket.Domain/bin /src/src/GenesisMarket.Domain/obj /src/src/GenesisMarket.Infrastructure/bin /src/src/GenesisMarket.Infrastructure/obj /src/src/GenesisMarket.Api/bin /src/src/GenesisMarket.Api/obj -mindepth 1 -delete"
	$(COMPOSE) up -d --build --force-recreate --no-deps api

# Полный сброс: удаляет контейнеры И named volumes (genesis-dev-postgres,
# genesis-dev-minio и т.д.) — все данные dev-БД и файлы MinIO теряются.
dev-reset:
	$(COMPOSE) down -v --remove-orphans
