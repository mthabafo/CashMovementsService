using CashMovements.Domain.Exceptions;
using System;

namespace CashMovements.Domain.Entities
{
    public class Movement
    {
        public long Id { get; private set; }

        public string AccountId { get; private set; }

        public string ExternalRef { get; private set; }

        public decimal Amount { get; private set; }

        public DateTimeOffset OccurredAt { get; private set; }

        public string Narration { get; private set; }

        private Movement(
            string accountId,
            string externalRef,
            decimal amount,
            DateTimeOffset occurredAt,
            string narration)
        {
            Id = Id;
            AccountId = accountId;
            ExternalRef = externalRef;
            Amount = amount;
            OccurredAt = occurredAt.ToUniversalTime();
            Narration = narration;
        }

        public static Movement Create(
            string accountId,
            string externalRef,
            decimal amount,
            DateTimeOffset occurredAt,
            string narration)
        {
            if (accountId == String.Empty)
                throw new InvalidMovementException(
                    "Account ID is required.");
            if (externalRef == String.Empty)
                throw new InvalidMovementException(
                    "External reference is required.");


            return new Movement(
                accountId,
                externalRef,
                amount,
                occurredAt,
                narration);
        }

    }
}
