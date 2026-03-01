.PHONY: infra migrate api worker-transfer worker-notify test build clean

# Start all Docker infrastructure services
infra:
	docker-compose up -d

# Stop infrastructure
infra-down:
	docker-compose down

# Run EF Core migrations
migrate:
	dotnet ef migrations add InitialCreate \
		--project src/BankingMessaging.Infrastructure \
		--startup-project src/BankingMessaging.Api \
		--no-build 2>/dev/null || true
	dotnet ef database update \
		--project src/BankingMessaging.Infrastructure \
		--startup-project src/BankingMessaging.Api

# Start REST API
api:
	dotnet run --project src/BankingMessaging.Api

# Start Transfer Worker
worker-transfer:
	dotnet run --project src/BankingMessaging.TransferWorker

# Start Notification Worker
worker-notify:
	dotnet run --project src/BankingMessaging.NotificationWorker

# Run all tests
test:
	dotnet test tests/BankingMessaging.IntegrationTests

# Build entire solution
build:
	dotnet build

# Clean build artifacts
clean:
	dotnet clean
