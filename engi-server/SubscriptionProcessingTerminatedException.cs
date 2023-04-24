namespace Engi.Substrate.Server;

public class SubscriptionProcessingTerminatedException : Exception
{
    public SubscriptionProcessingTerminatedException(string subscriptionName, Exception inner)
        : base($"Subscription '{subscriptionName}' processing was terminated.", inner)
    {
        
    }
}
