using ConsoleApp.Enums;
using ConsoleApp.Models;
using ConsoleApp.Repositories;
using ConsoleApp.Services;
using Moq;
using System.ComponentModel.DataAnnotations;

namespace ConsoleApp.Tests;

public class PaymentEvaluatorTests
{
    // -------------------------------------------------------------------------
    // Default test values – represent a safe, all-checks-passing baseline
    // -------------------------------------------------------------------------
    private static readonly Guid DefaultUserId   = Guid.NewGuid();
    private static readonly Guid DefaultTenantId = Guid.NewGuid();
    private const decimal DefaultAmount      = 100m;
    private const string  DefaultCurrency    = "USD";
    private const string  DefaultIpAddress   = "1.2.3.4";
    private const decimal DefaultDailyLimit  = 10_000m;

    // -------------------------------------------------------------------------
    // Mocks
    // -------------------------------------------------------------------------
    private readonly Mock<IUserRepository>       _userRepo            = new();
    private readonly Mock<IAccountRepository>    _accountRepo         = new();
    private readonly Mock<IRiskService>          _riskService         = new();
    private readonly Mock<IGeoService>           _geoService          = new();
    private readonly Mock<IFeatureFlagService>   _featureFlagService  = new();
    private readonly Mock<IPaymentRepository>    _paymentRepo         = new();
    private readonly Mock<IChargebackRepository> _chargebackRepo      = new();

    // -------------------------------------------------------------------------
    // Factory
    // -------------------------------------------------------------------------
    private PaymentEvaluator CreateSut() => new(
        _userRepo.Object,
        _accountRepo.Object,
        _riskService.Object,
        _geoService.Object,
        _featureFlagService.Object,
        _paymentRepo.Object,
        _chargebackRepo.Object);

    // -------------------------------------------------------------------------
    // SetupDefaults – configures all 7 mocks with baseline passing values.
    // Pass non-null arguments to override specific dependencies.
    // -------------------------------------------------------------------------
    private void SetupDefaults(
        User?                        user              = null,
        Account?                     account           = null,
        RiskProfile?                 risk              = null,
        GeoInfo?                     geo               = null,
        IReadOnlyCollection<string>? flags             = null,
        int                          transactionsToday = 0,
        bool                         hasOpenChargeback = false)
    {
        var effectiveUser    = user    ?? new User(DefaultUserId, DefaultTenantId, UserTier.Standard, isAdmin: false);
        var effectiveAccount = account ?? new Account(Guid.NewGuid(), DefaultUserId, AccountStatus.Active, "USD", DefaultDailyLimit);
        var effectiveRisk    = risk    ?? new RiskProfile(20);
        var effectiveGeo     = geo     ?? new GeoInfo("US", CountryRisk.Low, isVpn: false);
        var effectiveFlags   = flags   ?? Array.Empty<string>();

        _userRepo
            .Setup(x => x.GetByIdAsync(DefaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(effectiveUser);

        _accountRepo
            .Setup(x => x.GetByUserIdAsync(DefaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(effectiveAccount);

        _riskService
            .Setup(x => x.GetRiskProfileAsync(DefaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(effectiveRisk);

        _geoService
            .Setup(x => x.ResolveAsync(DefaultIpAddress, It.IsAny<CancellationToken>()))
            .ReturnsAsync(effectiveGeo);

        // Use It.IsAny<Guid>() so tests that swap the user object still match.
        _featureFlagService
            .Setup(x => x.GetFlagsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(effectiveFlags);

        _paymentRepo
            .Setup(x => x.CountTodayAsync(DefaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(transactionsToday);

        _chargebackRepo
            .Setup(x => x.HasOpenChargebackAsync(DefaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hasOpenChargeback);

        _paymentRepo
            .Setup(x => x.RecordAttemptAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // =========================================================================
    // GROUP 1 – Guard Clauses
    // =========================================================================

    [Fact]
    public async Task EvaluatePaymentAsync_UserNotFound_ThrowsArgumentException()
    {
        _userRepo
            .Setup(x => x.GetByIdAsync(DefaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((User?)null);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress));
    }

    [Fact]
    public async Task EvaluatePaymentAsync_AccountNotFound_ThrowsValidationException()
    {
        var user = new User(DefaultUserId, DefaultTenantId, UserTier.Standard, isAdmin: false);

        _userRepo
            .Setup(x => x.GetByIdAsync(DefaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        _accountRepo
            .Setup(x => x.GetByUserIdAsync(DefaultUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account?)null);

        await Assert.ThrowsAsync<ValidationException>(() =>
            CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress));
    }

    // =========================================================================
    // GROUP 2 – Node 1: VIP Skip (flag=skip_risk_checks_for_vip && Tier==VIP)
    // =========================================================================

    [Fact]
    public async Task EvaluatePaymentAsync_VipUserAndSkipFlag_ApprovesWithoutRiskChecks()
    {
        // MCDC: flag=T, tier=VIP → compound=T → early Approve
        var user = new User(DefaultUserId, DefaultTenantId, UserTier.VIP, isAdmin: false);
        SetupDefaults(user: user, flags: new[] { "skip_risk_checks_for_vip" });

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
        // Must short-circuit before RecordAttemptAsync
        _paymentRepo.Verify(
            x => x.RecordAttemptAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_VipUserWithoutSkipFlag_ContinuesToRiskChecks()
    {
        // MCDC: flag=F, tier=VIP → compound=F → falls through to final Approve
        var user = new User(DefaultUserId, DefaultTenantId, UserTier.VIP, isAdmin: false);
        SetupDefaults(user: user, flags: Array.Empty<string>());

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
        _paymentRepo.Verify(
            x => x.RecordAttemptAsync(DefaultUserId, DefaultAmount, DefaultCurrency, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_SkipFlagSetForNonVipUser_ContinuesToRiskChecks()
    {
        // MCDC: flag=T, tier=Premium (≠VIP) → compound=F → falls through
        var user = new User(DefaultUserId, DefaultTenantId, UserTier.Premium, isAdmin: false);
        SetupDefaults(user: user, flags: new[] { "skip_risk_checks_for_vip" });

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
        _paymentRepo.Verify(
            x => x.RecordAttemptAsync(DefaultUserId, DefaultAmount, DefaultCurrency, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // =========================================================================
    // GROUP 3 – Node 2: Account Status
    // =========================================================================

    [Fact]
    public async Task EvaluatePaymentAsync_BlockedAccount_NoAdminOverride_RejectsWithCorrectMessage()
    {
        // N2 outer: Blocked=T → inner: flag=F, admin=F → Reject
        var account = new Account(Guid.NewGuid(), DefaultUserId, AccountStatus.Blocked, "USD", DefaultDailyLimit);
        SetupDefaults(account: account);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Rejected, result.Status);
        Assert.Equal("Account is not allowed to make payments", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_ClosedAccount_NoAdminOverride_RejectsWithCorrectMessage()
    {
        // N2 outer: Closed=T → inner: both false → Reject
        var account = new Account(Guid.NewGuid(), DefaultUserId, AccountStatus.Closed, "USD", DefaultDailyLimit);
        SetupDefaults(account: account);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Rejected, result.Status);
        Assert.Equal("Account is not allowed to make payments", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_BlockedAccount_AdminUserWithOverrideFlag_ContinuesProcessing()
    {
        // N2 inner: flag=T, isAdmin=T → override allows processing → final Approve
        var user    = new User(DefaultUserId, DefaultTenantId, UserTier.Standard, isAdmin: true);
        var account = new Account(Guid.NewGuid(), DefaultUserId, AccountStatus.Blocked, "USD", DefaultDailyLimit);
        SetupDefaults(user: user, account: account, flags: new[] { "allow_admin_override" });

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_BlockedAccount_AdminUserWithoutOverrideFlag_RejectsPayment()
    {
        // MCDC N2 inner: isAdmin=T, flag=F → !(F&&T)=T → Reject
        var user    = new User(DefaultUserId, DefaultTenantId, UserTier.Standard, isAdmin: true);
        var account = new Account(Guid.NewGuid(), DefaultUserId, AccountStatus.Blocked, "USD", DefaultDailyLimit);
        SetupDefaults(user: user, account: account, flags: Array.Empty<string>());

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Rejected, result.Status);
        Assert.Equal("Account is not allowed to make payments", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_BlockedAccount_NonAdminWithOverrideFlag_RejectsPayment()
    {
        // MCDC N2 inner: isAdmin=F, flag=T → !(T&&F)=T → Reject
        var user    = new User(DefaultUserId, DefaultTenantId, UserTier.Standard, isAdmin: false);
        var account = new Account(Guid.NewGuid(), DefaultUserId, AccountStatus.Blocked, "USD", DefaultDailyLimit);
        SetupDefaults(user: user, account: account, flags: new[] { "allow_admin_override" });

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Rejected, result.Status);
        Assert.Equal("Account is not allowed to make payments", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_SuspendedAccount_ContinuesProcessing()
    {
        // N2 outer: Suspended → neither Blocked nor Closed → outer=F → skips block
        var account = new Account(Guid.NewGuid(), DefaultUserId, AccountStatus.Suspended, "USD", DefaultDailyLimit);
        SetupDefaults(account: account);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
    }

    // =========================================================================
    // GROUP 4 – Node 3: Daily Limit (amount >= DailyLimit && !allow_high_value)
    // =========================================================================

    [Fact]
    public async Task EvaluatePaymentAsync_AmountExactlyAtDailyLimit_NoHighValueFlag_RejectsPayment()
    {
        // Boundary: >= operator fires at the exact limit value
        var account = new Account(Guid.NewGuid(), DefaultUserId, AccountStatus.Active, "USD", 500m);
        SetupDefaults(account: account);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, 500m, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Rejected, result.Status);
        Assert.Equal("Payment exceeds allowed limit", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_AmountExceedsDailyLimit_WithHighValueFlag_ContinuesProcessing()
    {
        // MCDC N3: amount>=limit=T, !flag=F → compound=F → continues
        var account = new Account(Guid.NewGuid(), DefaultUserId, AccountStatus.Active, "USD", 500m);
        SetupDefaults(account: account, flags: new[] { "allow_high_value_payments" });

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, 600m, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_AmountBelowDailyLimit_ContinuesProcessing()
    {
        // Boundary: one unit below limit → condition false → continues
        var account = new Account(Guid.NewGuid(), DefaultUserId, AccountStatus.Active, "USD", 500m);
        SetupDefaults(account: account);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, 499m, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
    }

    // =========================================================================
    // GROUP 5 – Node 4: Cross-Currency
    // =========================================================================

    [Fact]
    public async Task EvaluatePaymentAsync_CrossCurrency_NoFlag_HighCountryRisk_HighTransactions_Escalates()
    {
        // N4 outer I=T (currency differs, no flag); geo=High; N4inner tx>2 → Escalate
        var geo = new GeoInfo("RU", CountryRisk.High, isVpn: false);
        SetupDefaults(geo: geo, transactionsToday: 3);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, "EUR", DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Escalated, result.Status);
        Assert.Equal("Cross-currency payment requires manual review", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_CrossCurrency_NoFlag_HighCountryRisk_HighRiskScore_Escalates()
    {
        // N4 outer I=T; geo=High; N4inner risk>75 → Escalate (tx-path false)
        var risk = new RiskProfile(80);
        var geo  = new GeoInfo("RU", CountryRisk.High, isVpn: false);
        SetupDefaults(risk: risk, geo: geo, transactionsToday: 1);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, "EUR", DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Escalated, result.Status);
        Assert.Equal("Cross-currency payment requires manual review", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_CrossCurrency_NoFlag_HighCountryRisk_LowTransactionsAndLowRisk_Rejects()
    {
        // N4 outer I=T; geo=High; N4inner tx≤2 AND risk≤75 → Reject
        var risk = new RiskProfile(30);
        var geo  = new GeoInfo("RU", CountryRisk.High, isVpn: false);
        SetupDefaults(risk: risk, geo: geo, transactionsToday: 1);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, "EUR", DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Rejected, result.Status);
        Assert.Equal("Cross-currency payment not allowed", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_CrossCurrency_NoFlag_VpnDetected_HighTransactions_Escalates()
    {
        // N4 geo triggered via VPN alone (not high-risk country)
        var geo = new GeoInfo("US", CountryRisk.Low, isVpn: true);
        SetupDefaults(geo: geo, transactionsToday: 3);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, "EUR", DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Escalated, result.Status);
        Assert.Equal("Cross-currency payment requires manual review", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_CrossCurrency_NoFlag_OpenChargeback_HighTransactions_Escalates()
    {
        // N4 geo triggered via open chargeback alone
        var geo = new GeoInfo("US", CountryRisk.Low, isVpn: false);
        SetupDefaults(geo: geo, transactionsToday: 3, hasOpenChargeback: true);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, "EUR", DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Escalated, result.Status);
        Assert.Equal("Cross-currency payment requires manual review", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_CrossCurrency_NoFlag_LowGeoRisk_NoChargeback_ContinuesProcessing()
    {
        // N4 outer I=T but geo=Low, no VPN, no chargeback → geo condition false → falls through
        var geo = new GeoInfo("US", CountryRisk.Low, isVpn: false);
        SetupDefaults(geo: geo, transactionsToday: 1);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, "EUR", DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_CrossCurrency_AllowCrossCurrencyFlag_LowAmount_LowRisk_ContinuesProcessing()
    {
        // N4 outer I=F (flag set), J=F (amount≤1000) → outer=F → entire cross-currency block skipped
        SetupDefaults(flags: new[] { "allow_cross_currency" }, risk: new RiskProfile(30));

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, "EUR", DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_CrossCurrency_AllowCrossCurrencyFlag_HighAmountHighRisk_VpnDetected_Escalates()
    {
        // MCDC N4 outer J=T (flag set nullifies I but J: currDiff&&amt>1000&&risk>50 is independent)
        // geo triggered via VPN; tx>2 → Escalate
        var risk = new RiskProfile(60);
        var geo  = new GeoInfo("US", CountryRisk.Low, isVpn: true);
        SetupDefaults(risk: risk, geo: geo, transactionsToday: 3, flags: new[] { "allow_cross_currency" });

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, 1500m, "EUR", DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Escalated, result.Status);
        Assert.Equal("Cross-currency payment requires manual review", result.Reason);
    }

    // =========================================================================
    // GROUP 6 – Node 5: Fraud / High Risk
    // =========================================================================

    [Fact]
    public async Task EvaluatePaymentAsync_RiskScoreAbove85_HighCountryRisk_RejectsAsFraud()
    {
        // N5 outer P=T (risk>85 && High); inner: non-admin, no flag → Reject
        var risk = new RiskProfile(90);
        var geo  = new GeoInfo("RU", CountryRisk.High, isVpn: false);
        SetupDefaults(risk: risk, geo: geo);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Rejected, result.Status);
        Assert.Equal("Payment flagged as fraudulent", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_ExcessiveTransactions_RiskAbove70_RejectsAsFraud()
    {
        // MCDC N5 outer Q=T (tx>5 && risk>70), P=F (risk not >85 or country not High)
        var risk = new RiskProfile(75);
        var geo  = new GeoInfo("US", CountryRisk.Low, isVpn: false);
        SetupDefaults(risk: risk, geo: geo, transactionsToday: 6);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Rejected, result.Status);
        Assert.Equal("Payment flagged as fraudulent", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_FraudFlagged_AdminWithManualOverrideFlag_Escalates()
    {
        // N5 inner: IsAdmin=T, flag=T → Escalate instead of Reject
        var user = new User(DefaultUserId, DefaultTenantId, UserTier.Standard, isAdmin: true);
        var risk = new RiskProfile(90);
        var geo  = new GeoInfo("RU", CountryRisk.High, isVpn: false);
        SetupDefaults(user: user, risk: risk, geo: geo, flags: new[] { "allow_manual_override_for_risky_payments" });

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Escalated, result.Status);
        Assert.Equal("Privileged override required", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_FraudFlagged_AdminWithoutOverrideFlag_RejectsAsFraud()
    {
        // MCDC N5 inner: IsAdmin=T, flag=F → compound=F → Reject
        var user = new User(DefaultUserId, DefaultTenantId, UserTier.Standard, isAdmin: true);
        var risk = new RiskProfile(90);
        var geo  = new GeoInfo("RU", CountryRisk.High, isVpn: false);
        SetupDefaults(user: user, risk: risk, geo: geo, flags: Array.Empty<string>());

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Rejected, result.Status);
        Assert.Equal("Payment flagged as fraudulent", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_FraudFlagged_NonAdminWithOverrideFlag_RejectsAsFraud()
    {
        // MCDC N5 inner: IsAdmin=F, flag=T → compound=F → Reject
        var user = new User(DefaultUserId, DefaultTenantId, UserTier.Standard, isAdmin: false);
        var risk = new RiskProfile(90);
        var geo  = new GeoInfo("RU", CountryRisk.High, isVpn: false);
        SetupDefaults(user: user, risk: risk, geo: geo, flags: new[] { "allow_manual_override_for_risky_payments" });

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Rejected, result.Status);
        Assert.Equal("Payment flagged as fraudulent", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_RiskScoreExactly85_HighCountryRisk_ContinuesProcessing()
    {
        // Boundary: risk > 85 is false at score=85 → P=F; Q=F (0 tx) → N5 skipped
        var risk = new RiskProfile(85);
        var geo  = new GeoInfo("RU", CountryRisk.High, isVpn: false);
        SetupDefaults(risk: risk, geo: geo);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_ExactlyFiveTransactions_RiskAbove70_ContinuesProcessing()
    {
        // Boundary: transactionsToday > 5 is false at 5 → Q=F; P=F (low country) → N5 skipped
        var risk = new RiskProfile(75);
        var geo  = new GeoInfo("US", CountryRisk.Low, isVpn: false);
        SetupDefaults(risk: risk, geo: geo, transactionsToday: 5);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
    }

    // =========================================================================
    // GROUP 7 – Node 6: Large Payment
    // =========================================================================

    [Fact]
    public async Task EvaluatePaymentAsync_LargeAmountNonPremiumUser_HighRiskMediumCountry_Escalates()
    {
        // N6 outer S=T (>5000 && !Premium); N6 inner: risk>60 && Medium → Escalate
        var user = new User(DefaultUserId, DefaultTenantId, UserTier.Standard, isAdmin: false, roles: new[] { Role.Finance });
        var risk = new RiskProfile(65);
        var geo  = new GeoInfo("DE", CountryRisk.Medium, isVpn: false);
        SetupDefaults(user: user, risk: risk, geo: geo);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, 5001m, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Escalated, result.Status);
        Assert.Equal("Large payment requires additional approval", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_VeryLargeAmountNoFinanceRole_HighRiskHighCountry_Escalates()
    {
        // MCDC N6 outer T=T (>10000 && !Finance), S=F (Premium); N6 inner: risk>60 && High → Escalate
        // Account limit set to 50 000 so N3 (amount >= DailyLimit) does not fire first.
        var user    = new User(DefaultUserId, DefaultTenantId, UserTier.Premium, isAdmin: false);
        var account = new Account(Guid.NewGuid(), DefaultUserId, AccountStatus.Active, "USD", 50_000m);
        var risk    = new RiskProfile(65);
        var geo     = new GeoInfo("RU", CountryRisk.High, isVpn: false);
        SetupDefaults(user: user, account: account, risk: risk, geo: geo);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, 10001m, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Escalated, result.Status);
        Assert.Equal("Large payment requires additional approval", result.Reason);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_LargeAmountNonPremiumUser_LowRiskScore_ContinuesProcessing()
    {
        // N6 outer S=T but inner: risk≤60 → N6 inner=F → falls through to Approve
        var user = new User(DefaultUserId, DefaultTenantId, UserTier.Standard, isAdmin: false);
        var risk = new RiskProfile(55);
        var geo  = new GeoInfo("DE", CountryRisk.Medium, isVpn: false);
        SetupDefaults(user: user, risk: risk, geo: geo);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, 5001m, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_LargeAmountNonPremiumUser_HighRisk_LowCountryRisk_ContinuesProcessing()
    {
        // N6 outer S=T but inner: country=Low → (Medium||High)=F → N6 inner=F → falls through
        var user = new User(DefaultUserId, DefaultTenantId, UserTier.Standard, isAdmin: false);
        var risk = new RiskProfile(65);
        var geo  = new GeoInfo("US", CountryRisk.Low, isVpn: false);
        SetupDefaults(user: user, risk: risk, geo: geo);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, 5001m, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
    }

    [Fact]
    public async Task EvaluatePaymentAsync_LargeAmountPremiumUserWithFinanceRole_ContinuesProcessing()
    {
        // N6 outer: S=F (Premium → tier==Premium), T=F (≤10000 && has Finance) → outer=F → skip entirely
        var user = new User(DefaultUserId, DefaultTenantId, UserTier.Premium, isAdmin: false, roles: new[] { Role.Finance });
        var risk = new RiskProfile(65);
        var geo  = new GeoInfo("DE", CountryRisk.Medium, isVpn: false);
        SetupDefaults(user: user, risk: risk, geo: geo);

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, 7500m, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
    }

    // =========================================================================
    // GROUP 8 – Happy Path
    // =========================================================================

    [Fact]
    public async Task EvaluatePaymentAsync_AllChecksPass_ApprovesAndRecordsAttempt()
    {
        SetupDefaults();

        var result = await CreateSut().EvaluatePaymentAsync(DefaultUserId, DefaultAmount, DefaultCurrency, DefaultIpAddress);

        Assert.Equal(PaymentDecisionStatus.Approved, result.Status);
        _paymentRepo.Verify(
            x => x.RecordAttemptAsync(DefaultUserId, DefaultAmount, DefaultCurrency, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
