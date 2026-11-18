using System;
using System.Collections.Generic;
using System.Text;

namespace CashMovements.Domain.Exceptions
{
    public sealed class InvalidMovementException
    : DomainException
    {
        public InvalidMovementException(string message)
            : base(message)
        {
        }
    }
}
