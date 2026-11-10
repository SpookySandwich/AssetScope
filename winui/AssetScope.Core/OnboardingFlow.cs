namespace AssetScope.Core;

/// <summary>
/// First-run setup is a guide, not a lock. Plaid and SnapTrade are independent
/// optional paths; credentials are only requested when that provider is not
/// already configured. A manual balance or skip also completes the guide.
/// </summary>
public enum OnboardingStep
{
    ChooseMethod,
    PlaidCredentials,
    BankConnect,
    SnapTradeCredentials,
    BrokerageConnect,
    Done,
}

public sealed class OnboardingFlow
{
    public OnboardingStep Step { get; private set; } = OnboardingStep.ChooseMethod;

    public OnboardingStep ChoosePlaid(bool configured) =>
        Step = configured ? OnboardingStep.BankConnect : OnboardingStep.PlaidCredentials;

    public OnboardingStep ChooseSnapTrade(bool configured) =>
        Step = configured ? OnboardingStep.BrokerageConnect : OnboardingStep.SnapTradeCredentials;

    public OnboardingStep PlaidCredentialsSaved() => Step = OnboardingStep.BankConnect;

    public OnboardingStep SnapTradeCredentialsSaved() => Step = OnboardingStep.BrokerageConnect;

    public OnboardingStep Complete() => Step = OnboardingStep.Done;

    public OnboardingStep ChooseAnotherAutomaticConnection() => Step = OnboardingStep.BankConnect;

    public OnboardingStep ChooseAnotherBrokerage() => Step = OnboardingStep.BrokerageConnect;

    public OnboardingStep BackToMethods() => Step = OnboardingStep.ChooseMethod;
}
