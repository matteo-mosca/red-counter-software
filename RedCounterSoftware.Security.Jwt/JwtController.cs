﻿namespace RedCounterSoftware.Security.Jwt
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    using Common.Account;
    using Common.Logging;
    using Common.Mailing;

    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Extensions.Logging;

    using RedCounterSoftware.Logging.Web;

    [AllowAnonymous]
    public abstract class JwtController : Controller
    {
        private readonly IAuthenticationService authenticationService;

        private readonly IRoleService roleService;

        private readonly IPersonService personService;

        private readonly IMailingService mailingService;

        private readonly ILogger<JwtController> logger;

        private readonly string jwtKey;

        private readonly string jwtIssuer;

        private readonly string jwtAudience;

        private readonly string passwordResetSubject;

        private readonly string passwordResetTextBody;

        private readonly string passwordResetHtmlBody;

        protected JwtController(
            IAuthenticationService authenticationService,
            IRoleService roleService,
            IPersonService personService,
            IMailingService mailingService,
            ILogger<JwtController> logger,
            string jwtKey,
            string jwtIssuer,
            string jwtAudience,
            string passwordResetSubject,
            string passwordResetTextBody,
            string passwordResetHtmlBody)
        {
            this.authenticationService = authenticationService ?? throw new ArgumentNullException(nameof(authenticationService));

            this.roleService = roleService ?? throw new ArgumentNullException(nameof(roleService));

            this.personService = personService ?? throw new ArgumentNullException(nameof(personService));

            this.mailingService = mailingService ?? throw new ArgumentNullException(nameof(mailingService));

            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));

            this.jwtKey = jwtKey ?? throw new ArgumentNullException(nameof(jwtKey));

            this.jwtIssuer = jwtIssuer ?? throw new ArgumentNullException(nameof(jwtIssuer));

            this.jwtAudience = jwtAudience ?? throw new ArgumentNullException(nameof(jwtAudience));

            this.passwordResetSubject = passwordResetSubject ?? throw new ArgumentNullException(nameof(passwordResetSubject));

            this.passwordResetTextBody = passwordResetTextBody ?? throw new ArgumentNullException(nameof(passwordResetTextBody));

            this.passwordResetHtmlBody = passwordResetHtmlBody ?? throw new ArgumentNullException(nameof(passwordResetHtmlBody));
        }

        [HttpPost("activate")]
        public async Task<IActionResult> ActivateUser([FromBody]ActivateUserModel model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            using (this.logger.BeginScope(LoggingEvents.Activation))
            using (this.logger.ScopeRemoteIp(this.HttpContext))
            {
                var result = await this.authenticationService.Activate(model.Id);

                if (!result.IsValid)
                {
                    this.logger.LogInformation(LoggingEvents.ActivationFail, "An activation attempt with an invalid code [{code}] has been made", model.Id);
                    return this.NotFound();
                }

                await this.authenticationService.SetPassword(result.Item.Id, model.Password);
                this.logger.LogInformation(LoggingEvents.ActivationOk, "User {user} with activation code [{code}] created a password and activated succesfully", result.Item.Email, model.Id);
                return this.Ok();
            }
        }

        [HttpPost("createtoken")]
        public async Task<IActionResult> CreateToken([FromBody]LoginModel loginModel)
        {
            if (loginModel == null)
            {
                throw new ArgumentNullException(nameof(loginModel));
            }

            using (this.logger.BeginScope(LoggingEvents.Authentication))
            using (this.logger.ScopeRemoteIp(this.HttpContext))
            {
                this.logger.LogInformation(LoggingEvents.Authentication, "JWT Token requested for user {user}", loginModel.Username);

                var isAuthorized = await this.authenticationService.IsPasswordValid(loginModel.Username, loginModel.Password);

                if (!isAuthorized)
                {
                    this.logger.LogInformation(LoggingEvents.AuthenticationFail, "Invalid credentials for user {user}", loginModel.Username);
                    return this.Unauthorized();
                }

                var user = await this.authenticationService.GetByEmail(loginModel.Username, CancellationToken.None);

                var isValidStatus = await this.authenticationService.IsUserActive(user);

                if (!isValidStatus)
                {
                    this.logger.LogInformation(LoggingEvents.AuthenticationFail, "User {user} is inactive or locked out", loginModel.Username);
                    return this.Forbid();
                }

                var token = await this.CreateToken(loginModel, user, true);
                var lightweightToken = await this.CreateToken(loginModel, user, false);

                return this.Ok(new JwtModel { ExpiresAt = token.ExpiresAt, Token = token.Token, LightweightToken = lightweightToken.Token });
            }
        }

        [HttpPost("requestresetpassword")]
        public async Task<IActionResult> SendPasswordResetMail([FromBody] PasswordResetRequestModel model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            using (this.logger.BeginScope(LoggingEvents.ResetPassword))
            using (this.logger.ScopeRemoteIp(this.HttpContext))
            {
                var user = await this.authenticationService.GetByEmail(model.Email, CancellationToken.None);
                if (user == null)
                {
                    this.logger.LogInformation(LoggingEvents.ResetPassword, "Attempted to reset password for non existing user {user}", model.Email);

                    // Intenzionale: non vogliamo far sapere a un malintenzionato se un utenza esiste o meno a sistema.
                    return this.Ok();
                }

                var guid = Guid.NewGuid();
                var result = await this.authenticationService.SetPasswordResetGuid(user.Id, guid, CancellationToken.None);
                this.logger.LogInformation(LoggingEvents.ResetPassword, "Set password reset guid for user {user} to [{id}]", model.Email, guid);

                user = result.Item;

                await this.mailingService.SendPasswordRecoveryMail(user.Email, guid, this.passwordResetSubject, this.passwordResetTextBody, this.passwordResetHtmlBody, CancellationToken.None);
                this.logger.LogInformation(LoggingEvents.ResetPassword, "Reset password email sent for user {user} with reset guid [{id}]", model.Email, guid);

                return this.Ok();
            }
        }

        [HttpPost("resetpassword")]
        public async Task<IActionResult> ResetPassword([FromBody] PasswordResetModel model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            using (this.logger.BeginScope(LoggingEvents.ResetPassword))
            using (this.logger.ScopeRemoteIp(this.HttpContext))
            {
                var user = await this.authenticationService.GetByPasswordResetGuid(model.Id, CancellationToken.None);
                if (user == null)
                {
                    this.logger.LogInformation("No user found with matching password reset guid [{id}]", model.Id);
                    return this.NotFound();
                }

                var result = await this.authenticationService.SetPassword(user.Id, model.Password, CancellationToken.None);
                if (result.IsValid)
                {
                    return this.Ok();
                }

                this.logger.LogInformation("Invalid user input: {input}", result.Failures.Select(c => c.ErrorMessage).Aggregate((s1, s2) => s1 + Environment.NewLine + s2));
                return new StatusCodeResult(422);
            }
        }

        private async Task<JwtModel> CreateToken(LoginModel loginModel, IUser user, bool includeRoles)
        {
            if (loginModel == null)
            {
                throw new ArgumentNullException(nameof(loginModel));
            }

            var roles = includeRoles ? (await this.roleService.GetByUserId(user.Id)).SelectMany(c => c.Claims).ToArray() : new string[] { };
            var person = await this.personService.GetById(user.PersonId, CancellationToken.None);
            var token = JwtHelper.BuildToken(user, person, this.jwtKey, this.jwtIssuer, this.jwtAudience, roles);

            this.logger.LogInformation(LoggingEvents.AuthenticationOk, "Jwt Token created for user {user}", loginModel.Username);
            return token;
        }
    }
}
