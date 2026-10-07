Design Document
1. Overview
I built this as a modular ASP.NET Core backend, with Orders, Payments and Shipping as separate modules. PostgreSQL holds the transactional data and Kafka carries events between the modules, so slow work like shipping never holds up a customer who is placing an order.
In the sample code all the modules run inside one ASP.NET Core app. I kept them loosely coupled on purpose. Each has its own controller, service and repository, and they talk to each other through events, so pulling one out into its own service later wouldn't mean a rewrite.
This is the overall flow:
Web / Mobile Client
        |
| HTTPS + JWT
        v
   ASP.NET Core API
| +----+---------+----------------+ ||| vvv
Orders         Payments         Shipping
Service        Service          Service
||| |v|
   |        Payment Gateway        |
| (Mock / Stripe) | || +--------------+----------------+
|
v PostgreSQL
                  |
           Transactional
Outbox |
v
Kafka
| +-------+-------+ || vv
    Shipping Consumer   Other Consumers
          |
          v
   Shipping Provider
   (Mock / FedEx/UPS)
In production I would put Azure Front Door with a WAF and API Management in front of the API, serve catalog reads from Redis and a CDN, and use Azure services for analytics and autoscaling. Those pieces belong to the target architecture and are not part of the local sample.
What the sample code covers: JWT login and roles, orders with stock handling and idempotency, payments with retry and a circuit breaker against a mock gateway, and the transactional outbox. Events leave the outbox through an IEventPublisher interface, and the Kafka producer sits behind that interface. The shipping consumer is described here as design.
2. How an order flows
A customer browses the catalog and places an order from the web or mobile app. The request reaches the API, where JWT authentication, authorization and request validation run first.
Creating an order does these things in a single database transaction:
1. Check that the products exist and there is enough stock.
2. Save the order and its items.
3. Reduce the stock.
4. Write an OrderCreated event to the outbox table.
Then the API returns the order. Because the outbox row is saved in the same transaction, we never end up with an order and no event, or an event for an order that didn't save.
Clients can retry safely. Every order request carries an Idempotency-Key, so if the network drops and the app sends the request again, the customer gets the original order back instead of a duplicate.
Payment is a separate call. PaymentService talks to an IPaymentGateway interface, so the mock gateway can be swapped for Stripe or PayPal without touching the payment logic. We keep only the payment status and the provider's reference, never card details, which keeps our PCI-DSS scope small.
3. Payments and failure handling
Payment providers fail now and then, so every call goes through three protections:
    PaymentService
      |
      v
IPaymentGateway
      |
      +--> Retry
      |
      +--> Circuit Breaker
      |
      +--> Timeout
      |
      v
Stripe / PayPal
 Short failures are retried with exponential backoff and a little random jitter, so retries don't all land at the same moment. If the provider keeps failing, the circuit breaker opens and further requests fail immediately instead of piling onto a service that is already struggling.
Paying the same order twice is safe too. If a payment has already succeeded, the same result comes back and the customer isn't charged again. A unique constraint on OrderId in the database backs this up.
The outbox gives events the same kind of safety: business changes and their events are saved together and published afterwards.
4. Shipping and asynchronous processing
Shipping stays off the critical path. Placing an order shouldn't have to wait for a carrier API.
Once an OrderCreated event reaches Kafka, a shipping consumer picks it up and creates the shipment:
    OrderCreated
     |
     v
Transactional Outbox
|
v Kafka | v
Shipping Consumer
     |
     v
FedEx / UPS API
 Shipping goes through a provider interface. I would start with a mock provider and swap in FedEx or UPS later. A shipment records the carrier, tracking number, estimated delivery date and status.
Kafka delivers at least once, so a consumer can see the same event twice and has to cope with that. Messages that keep failing are retried a few times and then moved to a dead-letter topic, so one bad message can't block the queue.

5. Real-time order status
For live updates I would keep backend messaging and browser delivery separate. Kafka suits the backend because several services can read the same event stream. A browser shouldn't connect to Kafka, so a notification service reads the events and pushes them out over SignalR or WebSockets:
    Order Status Changed
        |
v Kafka | v
Notification Service
        |
        v
Azure SignalR / WebSocket
|
        v
Customer Browser
 So Kafka handles reliable delivery between services, and SignalR handles the last hop to the user. If the socket drops, the client can fall back to polling the order status. The assignment's bonus question points the same way: a durable stream behind the scenes and WebSockets or SignalR for the browser.
6. Scalability
The target is 10M+ orders a day and 100K+ concurrent users. That is about 116 orders per second on average, and roughly 1,200 per second if Black Friday runs at ten times normal traffic.
The main idea is to keep the order-placement path small and push everything else to asynchronous processing.
Catalog reads are the heaviest traffic and the easiest to cache, so Redis and a CDN take most of that load off the database. The API is stateless, so more instances can be added behind Azure's load balancing as traffic grows.
For PostgreSQL in production I would use:
• connection pooling, for example PgBouncer
• indexes that match the real queries, such as orders by customer and date
• read replicas for order history
• monthly partitions on the orders table
• autoscaling for storage and compute
• high availability across zones
Kafka also soaks up spikes. The payment, shipping and notification consumers work through the backlog at their own pace instead of slowing down order creation.
7. Data
PostgreSQL stores the transactional data in these tables:
Orders, OrderItems, Payments, Users, Products and the outbox are implemented in the sample. Shipments belongs to the shipping module in the design.
Products are read-heavy and rarely change, so they cache well.
Analytics data stays out of the transactional database. Events such as product views, conversions, order creation and successful payments would go to Kafka or Event Hubs and end up in a Data Lake for reporting and the admin dashboard.
    Users
Products
Orders
OrderItems
Payments
Shipments
OutboxMessages
 
8. Security Authentication
API requests are authenticated with JWT bearer tokens.
Authorization
Roles separate customers from admins. Customers can only see and change their own orders, and an order that belongs to someone else returns 404, so order IDs can't be guessed. Admin actions, such as moving an order to shipped, need the admin policy.
Payment security
Card details never reach our servers. In production the client would use Stripe's or PayPal's hosted payment fields, and we would store only the provider reference and the payment status.
API protection
Login and register have strict rate limits, and the rest of the API has a per-user limit. In production, API Management and the WAF add another layer in front.
Secrets
In Azure, connection strings, payment keys and the JWT signing key belong in Key Vault, not in source code or config files.
9. Cost
There is no point running everything at full capacity all day. On Azure I would use:
• Container Apps for the API and the background workers
• Azure Functions for small event-driven jobs
• Redis and a CDN, so the database can stay smaller
• autoscaling based on traffic and on the event backlog
• reserved capacity for the steady baseline load
• Data Lake lifecycle rules that move old analytics data to cheaper storage
That way the platform scales up for busy periods and costs much less in quiet ones.
10. Bonus questions Reducing cost
Caching, serverless for bursty work, autoscaling, and cheaper storage tiers for old analytics data save the most.
Kafka vs WebSockets
I don't see them as competing. Kafka moves events reliably between backend services. WebSockets or SignalR deliver the final status update to the browser. They solve different problems, and a real-time order status feature needs both.
