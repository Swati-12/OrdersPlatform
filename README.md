# Global E-Commerce Order Management Platform

Backend implementation for the Airmaster Coding Evaluation Exercise.

The solution is designed for a scalable global e-commerce platform handling products, orders, payments, shipping, authentication, asynchronous event processing, and fault-tolerant integrations.

## Technology Stack

- .NET 8 / ASP.NET Core Web API
- C#
- Entity Framework Core
- PostgreSQL
- Apache Kafka
- JWT Authentication
- Role-based Authorization
- FluentValidation
- Polly Resilience
- Swagger / OpenAPI
- NUnit
- Moq
- Docker

---

# Project Structure

```text
OrdersPlatform/
│
├── backend/
│   ├── src/
│   │   └── Orders.Api/
│   │       ├── Auth/
│   │       ├── Controllers/
│   │       ├── Data/
│   │       ├── Dtos/
│   │       ├── Entities/
│   │       ├── Messaging/
│   │       ├── Middleware/
│   │       ├── Migrations/
│   │       ├── Payments/
│   │       ├── Repositories/
│   │       ├── Services/
│   │       ├── Shipping/
│   │       ├── Validators/
│   │       ├── Program.cs
│   │       └── appsettings.json
│   │
│   └── tests/
│       └── Orders.Tests/
│
├── docs/
│   └── DESIGN.md
│
├── docker-compose.yml
└── README.md



Prerequisites
- .NET 8 SDK
- PostgreSQL
- Docker Desktop
- Git
Database Setup
Create a PostgreSQL database named:
orders

Configure the connection string using local configuration or environment variables.
Do not commit real passwords or secrets to GitHub.
Start Kafka
From the project root:
docker compose up -d

Kafka runs locally on:
localhost:9092

Run the Application
cd backend
dotnet restore
dotnet ef database update --project src/Orders.Api --startup-project src/Orders.Api
dotnet build
dotnet run --project src/Orders.Api

API:
http://localhost:5080

Swagger:
http://localhost:5080/swagger

Run Tests
cd backend
dotnet test

Event Flow
Order events are published to Kafka using the Transactional Outbox pattern.
Order
  ↓
Transactional Outbox
  ↓
Kafka
  ↓
OrderEventsConsumer
  ↓
Shipping Service

Kafka topics:
order-events
order-events.DLT

Consumer group:
orders-shipping

Payment
Payment processing is implemented behind an abstraction so that real providers such as Stripe or PayPal can be integrated without changing the core business logic.
The current implementation includes:
- Retry
- Exponential backoff
- Circuit breaker
- Timeout
- Duplicate payment protection
Shipping
Shipping is implemented behind the IShippingProvider abstraction.
The current evaluation implementation uses a mock provider and supports:
- Shipment creation
- Tracking number
- Carrier
- Estimated delivery
- Shipment status updates
API Examples
Authentication
POST /api/auth/register
POST /api/auth/login

Products
GET /api/products

Orders
POST /api/orders
GET /api/orders/{id}

Payment
POST /api/orders/{id}/pay

Shipment
GET /api/orders/{orderId}/shipment
PATCH /api/orders/{orderId}/shipment/status

Design & Architecture
Detailed architecture, scalability, fault tolerance, security, cost optimization, GDPR, analytics, and production considerations are documented separately in:
docs/DESIGN.md

Repository
https://github.com/Swati-12/OrdersPlatform

After saving it:

```bash
cd ~/OrdersPlatform
git add README.md
git commit -m "Add README"
git push
