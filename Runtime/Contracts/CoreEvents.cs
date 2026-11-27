using System;
using System.Collections.Generic;

namespace Asparagus.Sdk.Contracts
{
    /// <summary>
    /// Target sink for sending generic and outcome game events.
    /// </summary>
    public interface IEventSink
    {
        void SendEvent(string eventName, IReadOnlyDictionary<string, Value> eventData = null, string span = null);
        void SendOutcomeEvent(string eventName, string outcome, IReadOnlyDictionary<string, Value> eventData = null, string span = null);
    }

    /// <summary>
    /// Strongly-typed helper for sending all eleven core events defined in <c>core_events.json</c>.
    /// Reuses an internal scratch dictionary under a lightweight lock so core event calls incur
    /// zero per-call dictionary allocations while remaining thread-safe and non-throwing.
    /// </summary>
    public sealed class CoreEventSender
    {
        private readonly IEventSink _sink;
        private readonly object _scratchLock = new object();
        private readonly Dictionary<string, Value> _scratch = new Dictionary<string, Value>(8, StringComparer.Ordinal);

        public CoreEventSender(IEventSink sink)
        {
            _sink = sink;
        }

        public void LevelStarted(
            string level,
            int? attemptNumber = null,
            double? difficulty = null,
            string span = null)
        {
            if (_sink == null) return;

            try
            {
                lock (_scratchLock)
                {
                    _scratch.Clear();
                    if (level != null) _scratch["level"] = Value.FromText(level);
                    if (attemptNumber.HasValue) _scratch["attempt_number"] = Value.FromNumber(attemptNumber.Value);
                    if (difficulty.HasValue) _scratch["difficulty"] = Value.FromNumber(difficulty.Value);

                    _sink.SendEvent(
                        CoreEventNames.GameLevelStarted,
                        _scratch,
                        string.IsNullOrEmpty(span) ? CoreSpans.Level : span);
                    _scratch.Clear();
                }
            }
            catch
            {
                // Fault containment: never throw into host game code.
            }
        }

        public void LevelSucceeded(
            string level,
            double? score = null,
            double? duration = null,
            int? attemptNumber = null,
            string span = null)
        {
            if (_sink == null) return;

            try
            {
                lock (_scratchLock)
                {
                    _scratch.Clear();
                    if (level != null) _scratch["level"] = Value.FromText(level);
                    if (score.HasValue) _scratch["score"] = Value.FromNumber(score.Value);
                    if (duration.HasValue) _scratch["duration"] = Value.FromNumber(duration.Value);
                    if (attemptNumber.HasValue) _scratch["attempt_number"] = Value.FromNumber(attemptNumber.Value);

                    _sink.SendEvent(
                        CoreEventNames.GameLevelSucceeded,
                        _scratch,
                        string.IsNullOrEmpty(span) ? CoreSpans.Level : span);
                    _scratch.Clear();
                }
            }
            catch
            {
            }
        }

        public void LevelFailed(
            string level,
            double? score = null,
            double? duration = null,
            int? attemptNumber = null,
            string span = null)
        {
            if (_sink == null) return;

            try
            {
                lock (_scratchLock)
                {
                    _scratch.Clear();
                    if (level != null) _scratch["level"] = Value.FromText(level);
                    if (score.HasValue) _scratch["score"] = Value.FromNumber(score.Value);
                    if (duration.HasValue) _scratch["duration"] = Value.FromNumber(duration.Value);
                    if (attemptNumber.HasValue) _scratch["attempt_number"] = Value.FromNumber(attemptNumber.Value);

                    _sink.SendEvent(
                        CoreEventNames.GameLevelFailed,
                        _scratch,
                        string.IsNullOrEmpty(span) ? CoreSpans.Level : span);
                    _scratch.Clear();
                }
            }
            catch
            {
            }
        }

        public void AdStarted(
            string type,
            string placement = null,
            int? adsInSet = null,
            int? adNumber = null,
            string span = null)
        {
            if (_sink == null) return;

            try
            {
                lock (_scratchLock)
                {
                    _scratch.Clear();
                    if (!string.IsNullOrEmpty(type)) _scratch["type"] = Value.FromText(type);
                    if (!string.IsNullOrEmpty(placement)) _scratch["placement"] = Value.FromText(placement);
                    if (adsInSet.HasValue) _scratch["ads_in_set"] = Value.FromNumber(adsInSet.Value);
                    if (adNumber.HasValue) _scratch["ad_number"] = Value.FromNumber(adNumber.Value);

                    _sink.SendEvent(
                        CoreEventNames.AdStarted,
                        _scratch,
                        string.IsNullOrEmpty(span) ? CoreSpans.AdForType(type) : span);
                    _scratch.Clear();
                }
            }
            catch
            {
            }
        }

        public void AdFinished(
            string type,
            string placement = null,
            bool? completed = null,
            int? adsInSet = null,
            int? adNumber = null,
            string span = null)
        {
            if (_sink == null) return;

            try
            {
                lock (_scratchLock)
                {
                    _scratch.Clear();
                    if (!string.IsNullOrEmpty(type)) _scratch["type"] = Value.FromText(type);
                    if (!string.IsNullOrEmpty(placement)) _scratch["placement"] = Value.FromText(placement);
                    if (adsInSet.HasValue) _scratch["ads_in_set"] = Value.FromNumber(adsInSet.Value);
                    if (adNumber.HasValue) _scratch["ad_number"] = Value.FromNumber(adNumber.Value);
                    if (completed.HasValue) _scratch["completed"] = Value.FromFlag(completed.Value);

                    _sink.SendEvent(
                        CoreEventNames.AdFinished,
                        _scratch,
                        string.IsNullOrEmpty(span) ? CoreSpans.AdForType(type) : span);
                    _scratch.Clear();
                }
            }
            catch
            {
            }
        }

        public void AdRevenue(
            string type,
            double amount,
            string currency,
            string outcome = Outcomes.Converted,
            string network = null,
            string reason = null,
            string span = null)
        {
            if (_sink == null) return;

            try
            {
                lock (_scratchLock)
                {
                    _scratch.Clear();
                    if (!string.IsNullOrEmpty(type)) _scratch["type"] = Value.FromText(type);
                    if (!string.IsNullOrEmpty(network)) _scratch["network"] = Value.FromText(network);
                    _scratch["amount"] = Value.FromNumber(amount);
                    if (currency != null) _scratch["currency"] = Value.FromText(currency);
                    if (!string.IsNullOrEmpty(reason)) _scratch["reason"] = Value.FromText(reason);

                    _sink.SendOutcomeEvent(
                        CoreEventNames.AdRevenue,
                        outcome ?? Outcomes.Converted,
                        _scratch,
                        span);
                    _scratch.Clear();
                }
            }
            catch
            {
            }
        }

        public void Purchase(
            string product,
            double price,
            string currency,
            string outcome = Outcomes.Converted,
            string reason = null,
            int? quantity = null,
            string transactionId = null,
            string span = null)
        {
            if (_sink == null) return;

            try
            {
                lock (_scratchLock)
                {
                    _scratch.Clear();
                    if (product != null) _scratch["product"] = Value.FromText(product);
                    _scratch["price"] = Value.FromNumber(price);
                    if (currency != null) _scratch["currency"] = Value.FromText(currency);
                    if (!string.IsNullOrEmpty(reason)) _scratch["reason"] = Value.FromText(reason);
                    if (quantity.HasValue) _scratch["quantity"] = Value.FromNumber(quantity.Value);
                    if (!string.IsNullOrEmpty(transactionId)) _scratch["transaction_id"] = Value.FromText(transactionId);

                    _sink.SendOutcomeEvent(
                        CoreEventNames.InAppPurchase,
                        outcome ?? Outcomes.Converted,
                        _scratch,
                        span);
                    _scratch.Clear();
                }
            }
            catch
            {
            }
        }

        public void PurchaseViewOpened(
            string type,
            string product = null,
            double? amount = null,
            string currency = null,
            string span = null)
        {
            if (_sink == null) return;

            try
            {
                lock (_scratchLock)
                {
                    _scratch.Clear();
                    if (type != null) _scratch["type"] = Value.FromText(type);
                    if (!string.IsNullOrEmpty(product)) _scratch["product"] = Value.FromText(product);
                    if (amount.HasValue) _scratch["amount"] = Value.FromNumber(amount.Value);
                    if (!string.IsNullOrEmpty(currency)) _scratch["currency"] = Value.FromText(currency);

                    _sink.SendEvent(
                        CoreEventNames.InAppPurchaseViewOpened,
                        _scratch,
                        string.IsNullOrEmpty(span) ? CoreSpans.PurchaseView : span);
                    _scratch.Clear();
                }
            }
            catch
            {
            }
        }

        public void PurchaseViewClosed(
            string type,
            string product = null,
            double? amount = null,
            string currency = null,
            string reason = null,
            string span = null)
        {
            if (_sink == null) return;

            try
            {
                lock (_scratchLock)
                {
                    _scratch.Clear();
                    if (type != null) _scratch["type"] = Value.FromText(type);
                    if (!string.IsNullOrEmpty(product)) _scratch["product"] = Value.FromText(product);
                    if (amount.HasValue) _scratch["amount"] = Value.FromNumber(amount.Value);
                    if (!string.IsNullOrEmpty(currency)) _scratch["currency"] = Value.FromText(currency);
                    if (!string.IsNullOrEmpty(reason)) _scratch["reason"] = Value.FromText(reason);

                    _sink.SendEvent(
                        CoreEventNames.InAppPurchaseViewClosed,
                        _scratch,
                        string.IsNullOrEmpty(span) ? CoreSpans.PurchaseView : span);
                    _scratch.Clear();
                }
            }
            catch
            {
            }
        }

        public void GamblingRoundStarted(
            string gameType,
            string machine = null,
            double? betAmount = null,
            string betCurrency = null,
            bool? autoSpin = null,
            string span = null)
        {
            if (_sink == null) return;

            try
            {
                lock (_scratchLock)
                {
                    _scratch.Clear();
                    if (gameType != null) _scratch["game_type"] = Value.FromText(gameType);
                    if (!string.IsNullOrEmpty(machine)) _scratch["machine"] = Value.FromText(machine);
                    if (betAmount.HasValue) _scratch["bet_amount"] = Value.FromNumber(betAmount.Value);
                    if (!string.IsNullOrEmpty(betCurrency)) _scratch["bet_currency"] = Value.FromText(betCurrency);
                    if (autoSpin.HasValue) _scratch["auto_spin"] = Value.FromFlag(autoSpin.Value);

                    _sink.SendEvent(
                        CoreEventNames.GamblingRoundStarted,
                        _scratch,
                        string.IsNullOrEmpty(span) ? CoreSpans.Round : span);
                    _scratch.Clear();
                }
            }
            catch
            {
            }
        }

        public void GamblingRoundFinished(
            string gameType,
            string result,
            double? betAmount = null,
            string betCurrency = null,
            double? returnAmount = null,
            string returnCurrency = null,
            string machine = null,
            string span = null)
        {
            if (_sink == null) return;

            try
            {
                lock (_scratchLock)
                {
                    _scratch.Clear();
                    if (gameType != null) _scratch["game_type"] = Value.FromText(gameType);
                    if (result != null) _scratch["result"] = Value.FromText(result);
                    if (betAmount.HasValue) _scratch["bet_amount"] = Value.FromNumber(betAmount.Value);
                    if (!string.IsNullOrEmpty(betCurrency)) _scratch["bet_currency"] = Value.FromText(betCurrency);
                    if (returnAmount.HasValue) _scratch["return_amount"] = Value.FromNumber(returnAmount.Value);
                    if (!string.IsNullOrEmpty(returnCurrency)) _scratch["return_currency"] = Value.FromText(returnCurrency);
                    if (!string.IsNullOrEmpty(machine)) _scratch["machine"] = Value.FromText(machine);

                    _sink.SendEvent(
                        CoreEventNames.GamblingRoundFinished,
                        _scratch,
                        string.IsNullOrEmpty(span) ? CoreSpans.Round : span);
                    _scratch.Clear();
                }
            }
            catch
            {
            }
        }
    }
}
