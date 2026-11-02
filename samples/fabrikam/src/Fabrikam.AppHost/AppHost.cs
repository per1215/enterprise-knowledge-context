var builder = DistributedApplication.CreateBuilder(args);

var messaging = builder.AddRabbitMQ("messaging");
var sql = builder.AddPostgres("postgres");
var claimsDb = sql.AddDatabase("claimsdb");
var policyDb = sql.AddDatabase("policydb");
var paymentsDb = sql.AddDatabase("paymentsdb");

var policyApi = builder.AddProject<Projects.Policy_Api>("policy-api")
    .WithReference(policyDb);

var claimsApi = builder.AddProject<Projects.Claims_Api>("claims-api")
    .WithReference(claimsDb)
    .WithReference(messaging)
    .WithReference(policyApi);

builder.AddProject<Projects.Fraud_Worker>("fraud-worker")
    .WithReference(messaging);

builder.AddProject<Projects.Payments_Api>("payments-api")
    .WithReference(paymentsDb)
    .WithReference(messaging);

builder.AddProject<Projects.Notifications_Worker>("notifications-worker")
    .WithReference(messaging);

builder.AddProject<Projects.Portal_Web>("portal-web")
    .WithExternalHttpEndpoints()
    .WithReference(claimsApi);

builder.Build().Run();
