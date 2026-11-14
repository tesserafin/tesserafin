#pragma warning disable CS1591

using System;
using System.Threading.Tasks;
using Tesserafin.Data.Entities;
using Tesserafin.Model.Users;

namespace Tesserafin.Controller.Authentication
{
    public interface IPasswordResetProvider
    {
        string Name { get; }

        bool IsEnabled { get; }

        Task<ForgotPasswordResult> StartForgotPasswordProcess(User? user, string enteredUsername, bool isInNetwork);

        Task<PinRedeemResult> RedeemPasswordResetPin(string pin);
    }

#nullable disable
    public class PasswordPinCreationResult
    {
        public string PinFile { get; set; }

        public DateTime ExpirationDate { get; set; }
    }
}
