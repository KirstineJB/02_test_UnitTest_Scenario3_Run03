using ConsoleApp.Enums;
using ConsoleApp.Models;
using ConsoleApp.Repositories;
using System.ComponentModel.DataAnnotations;



namespace ConsoleApp.Services
{

    public sealed class PaymentEvaluator
    {
        private readonly IUserRepository _userRepository;
        private readonly IAccountRepository _accountRepository;
        private readonly IRiskService _riskService;
        private readonly IGeoService _geoService;
        private readonly IFeatureFlagService _featureFlagService;
        private readonly IPaymentRepository _paymentRepository;
        private readonly IChargebackRepository _chargebackRepository;

        public PaymentEvaluator(
            IUserRepository userRepository,
            IAccountRepository accountRepository,
            IRiskService riskService,
            IGeoService geoService,
            IFeatureFlagService featureFlagService,
            IPaymentRepository paymentRepository,
            IChargebackRepository chargebackRepository)
        {
            _userRepository = userRepository;
            _accountRepository = accountRepository;
            _riskService = riskService;
            _geoService = geoService;
            _featureFlagService = featureFlagService;
            _paymentRepository = paymentRepository;
            _chargebackRepository = chargebackRepository;
        }

        public async Task<PaymentDecision> EvaluatePaymentAsync(
                            Guid userId,
                            decimal amount,
                            string currency,
                            string ipAddress,
                            CancellationToken ct = default)
        {
            var user = await _userRepository.GetByIdAsync(userId, ct)
                ?? throw new ArgumentException("User not found");

            var account = await _accountRepository.GetByUserIdAsync(userId, ct)
                ?? throw new ValidationException("Account not found");

            var risk = await _riskService.GetRiskProfileAsync(userId, ct);
            var geo = await _geoService.ResolveAsync(ipAddress, ct);
            var flags = await _featureFlagService.GetFlagsAsync(user.TenantId, ct);
            var transactionsToday = await _paymentRepository.CountTodayAsync(userId, ct);
            var hasOpenChargeback = await _chargebackRepository.HasOpenChargebackAsync(userId, ct);

         
            if (flags.Contains("skip_risk_checks_for_vip") && user.Tier == UserTier.VIP)
            {
                return PaymentDecision.Approve();
            }

            if (account.Status == AccountStatus.Blocked || account.Status == AccountStatus.Closed)
            {
                if (!(flags.Contains("allow_admin_override") && user.IsAdmin))
                {
                    return PaymentDecision.Reject("Account is not allowed to make payments");
                }
            }

            if (amount >= account.DailyLimit && !flags.Contains("allow_high_value_payments"))
            {
                return PaymentDecision.Reject("Payment exceeds allowed limit");
            }

            if ((currency != account.BaseCurrency && !flags.Contains("allow_cross_currency")) ||
                (currency != account.BaseCurrency && amount > 1000 && risk.Score > 50))
            {
                if (geo.CountryRisk == CountryRisk.High || geo.IsVpn || hasOpenChargeback)
                {
                    if (transactionsToday > 2 || risk.Score > 75)
                    {
                        return PaymentDecision.Escalate("Cross-currency payment requires manual review");
                    }
                    else
                    {
                        return PaymentDecision.Reject("Cross-currency payment not allowed");
                    }
                }
            }

            if ((risk.Score > 85 && geo.CountryRisk == CountryRisk.High) ||
                (transactionsToday > 5 && risk.Score > 70))
            {
                if (user.IsAdmin && flags.Contains("allow_manual_override_for_risky_payments"))
                {
                    return PaymentDecision.Escalate("Privileged override required");
                }

                return PaymentDecision.Reject("Payment flagged as fraudulent");
            }

            if ((amount > 5000 && user.Tier != UserTier.Premium) ||
                (amount > 10000 && !user.HasRole(Role.Finance)))
            {
                if (risk.Score > 60 && (geo.CountryRisk == CountryRisk.Medium || geo.CountryRisk == CountryRisk.High))
                {
                    return PaymentDecision.Escalate("Large payment requires additional approval");
                }
            }

            await _paymentRepository.RecordAttemptAsync(userId, amount, currency, ct);

            return PaymentDecision.Approve();
        }


    }
}
