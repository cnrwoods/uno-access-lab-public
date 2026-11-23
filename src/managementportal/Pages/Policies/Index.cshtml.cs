using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PolicyApi;

namespace ManagementPortal.Pages.Policies;

public sealed class IndexModel(IPolicyStore policyStore, PinProtection pinProtection, ILogger<IndexModel> logger) : PageModel
{
    // Each handler binds its own input. Activation and PIN reset must not
    // validate an unrelated, empty create form.
    public CreatePolicyInput Input { get; set; } = new();
    public ResetPinInput Reset { get; set; } = new();

    [TempData]
    public string? SuccessMessage { get; set; }

    public IReadOnlyList<AccessPolicy> Policies { get; private set; } = [];
    public bool PoliciesAvailable { get; private set; }
    public bool PinConfigured => pinProtection.IsConfigured;
    public AccessPolicy? ActivePolicy => Policies.FirstOrDefault(policy => policy.Enabled);
    public string? SelectedPreset { get; private set; }

    public static IReadOnlyList<PolicyPreset> Presets { get; } =
    [
        new("fingerprint-or-pin", "Fingerprint or PIN", "Either method, at any time.", "fingerprintOrPin", "any", "00:00", "24:00"),
        new("pin-only", "PIN only", "Require the policy PIN.", "pinOnly", "any", "00:00", "24:00"),
        new("fingerprint-only", "Fingerprint only", "Require an enrolled fingerprint.", "fingerprintOnly", "any", "00:00", "24:00"),
        new("even-minute", "Even minutes", "Fingerprint access on even minutes.", "fingerprintOnly", "even", "00:00", "24:00"),
        new("odd-minute", "Odd minutes", "Fingerprint access on odd minutes.", "fingerprintOnly", "odd", "00:00", "24:00"),
        new("daytime", "Daytime access", "Fingerprint access from 09:00 to 17:00.", "fingerprintOnly", "any", "09:00", "17:00"),
        new("deny-all", "Deny all access", "Keep access closed for every method.", "denyAll", "any", "00:00", "24:00")
    ];

    public async Task OnGetAsync(string? preset, CancellationToken cancellationToken)
    {
        Input = DefaultInput();
        var selection = Presets.FirstOrDefault(item => item.Id == preset);
        if (selection is not null)
        {
            SelectedPreset = selection.Id;
            Input.AccessMode = selection.AccessMode;
            Input.AllowedMinuteParity = selection.Parity;
            Input.StartTime = selection.StartTime;
            Input.EndTime = selection.EndTime;
        }

        await LoadPoliciesAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await TryUpdateModelAsync(Input, nameof(Input));
            var startMinute = ParseTime(Input.StartTime, false);
            var endMinute = ParseTime(Input.EndTime, true);
            if (startMinute is null)
            {
                ModelState.AddModelError("Input.StartTime", "Enter a start time from 00:00 to 23:59.");
            }
            if (endMinute is null)
            {
                ModelState.AddModelError("Input.EndTime", "Enter an end time from 00:01 to 24:00.");
            }
            if (startMinute is not null && endMinute is not null && startMinute >= endMinute)
            {
                ModelState.AddModelError("Input.EndTime", "The end time must be after the start time on the same day.");
            }

            if (AllowsPin(Input.AccessMode))
            {
                if (!PinConfigured)
                {
                    ModelState.AddModelError("Input.Pin", "Set up PIN protection before creating a policy that accepts a PIN.");
                }
                else
                {
                    ValidatePinPair(Input.Pin, Input.ConfirmPin, nameof(Input));
                }
            }
            else if (!string.IsNullOrEmpty(Input.Pin) || !string.IsNullOrEmpty(Input.ConfirmPin))
            {
                ModelState.AddModelError("Input.Pin", "Leave the PIN fields empty for a policy that does not accept a PIN.");
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var credential = AllowsPin(Input.AccessMode) ? pinProtection.Protect(Input.PolicyId, Input.Pin!) : null;
                    var policy = new AccessPolicy(Input.PolicyId, 1, Input.MakeActive, Input.AllowedMinuteParity,
                        SchemaVersion: 2, AccessMode: Input.AccessMode, StartMinute: startMinute!.Value,
                        EndMinute: endMinute!.Value, Pin: credential);
                    if (await policyStore.CreateAsync(policy, cancellationToken))
                    {
                        SuccessMessage = Input.MakeActive
                            ? $"Policy {policy.PolicyId} created and made active. It replaces the previous active policy."
                            : $"Policy {policy.PolicyId} created. Activate it when you are ready to use it.";
                        return RedirectToPage();
                    }

                    ModelState.AddModelError("Input.PolicyId", "A policy with this ID already exists. Choose a different ID.");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Could not create an access policy.");
                    ModelState.AddModelError(string.Empty, "The policy could not be saved. Please try again.");
                }
            }

            return await RedisplayAsync(cancellationToken);
        }
        finally
        {
            ClearPinValues();
        }
    }

    public async Task<IActionResult> OnPostRemoveAsync(string? policyId, CancellationToken cancellationToken)
    {
        Input = DefaultInput();
        var normalizedId = (policyId ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalizedId))
        {
            ModelState.AddModelError(string.Empty, "Choose a policy to remove.");
            return await RedisplayAsync(cancellationToken);
        }

        try
        {
            if (await policyStore.RemoveAsync(normalizedId, cancellationToken))
            {
                SuccessMessage = $"Policy {normalizedId} removed from the policy list.";
                return RedirectToPage();
            }

            ModelState.AddModelError(string.Empty, "That policy could not be found. Reload the list and try again.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not remove an access policy.");
            ModelState.AddModelError(string.Empty, "The policy could not be removed. Please try again.");
        }

        return await RedisplayAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostActivateAsync(string? policyId, CancellationToken cancellationToken)
    {
        Input = DefaultInput();
        var normalizedId = (policyId ?? string.Empty).Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalizedId))
        {
            ModelState.AddModelError(string.Empty, "Choose a policy to activate.");
            return await RedisplayAsync(cancellationToken);
        }

        try
        {
            if (await policyStore.SetActiveAsync(normalizedId, cancellationToken))
            {
                SuccessMessage = $"Policy {normalizedId} is now active. All other policies are inactive.";
                return RedirectToPage();
            }

            ModelState.AddModelError(string.Empty, "That policy could not be found. Reload the list and try again.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not activate an access policy.");
            ModelState.AddModelError(string.Empty, "The policy could not be activated. Please try again.");
        }

        return await RedisplayAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostPauseAsync(CancellationToken cancellationToken)
    {
        Input = DefaultInput();
        try
        {
            if (await policyStore.SetActiveAsync(null, cancellationToken))
            {
                SuccessMessage = "Access paused. No policy is active.";
                return RedirectToPage();
            }

            ModelState.AddModelError(string.Empty, "Access could not be paused. Please try again.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not pause access policies.");
            ModelState.AddModelError(string.Empty, "Access could not be paused. Please try again.");
        }

        return await RedisplayAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostResetPinAsync(CancellationToken cancellationToken)
    {
        Input = DefaultInput();
        try
        {
            await TryUpdateModelAsync(Reset, nameof(Reset));
            if (!PinConfigured)
            {
                ModelState.AddModelError("Reset.Pin", "Set up PIN protection before setting a policy PIN.");
            }
            else
            {
                ValidatePinPair(Reset.Pin, Reset.ConfirmPin, nameof(Reset));
            }

            if (ModelState.IsValid)
            {
                try
                {
                    var policy = (await policyStore.GetAllAsync(cancellationToken))
                        .FirstOrDefault(item => string.Equals(item.PolicyId, Reset.PolicyId, StringComparison.OrdinalIgnoreCase));
                    if (policy is null)
                    {
                        ModelState.AddModelError(string.Empty, "That policy could not be found. Reload the list and try again.");
                    }
                    else if (!AllowsPin(policy.AccessMode))
                    {
                        ModelState.AddModelError(string.Empty, "This policy does not accept a PIN.");
                    }
                    else if (await policyStore.UpdatePinAsync(policy.PolicyId, pinProtection.Protect(policy.PolicyId, Reset.Pin!), cancellationToken))
                    {
                        SuccessMessage = $"PIN updated for {policy.PolicyId}. The policy version has increased.";
                        return RedirectToPage();
                    }
                    else
                    {
                        ModelState.AddModelError(string.Empty, "That policy could not be found. Reload the list and try again.");
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "Could not update a policy PIN.");
                    ModelState.AddModelError(string.Empty, "The PIN could not be updated. Please try again.");
                }
            }

            return await RedisplayAsync(cancellationToken);
        }
        finally
        {
            ClearPinValues();
        }
    }

    private void ValidatePinPair(string? pin, string? confirmation, string prefix)
    {
        if (pin is null || pin.Length is < 4 or > 8 || pin.Any(character => character is < '0' or > '9'))
        {
            ModelState.AddModelError($"{prefix}.Pin", "Enter a PIN of 4 to 8 digits (0–9).");
        }
        if (string.IsNullOrEmpty(confirmation) || !string.Equals(pin, confirmation, StringComparison.Ordinal))
        {
            ModelState.AddModelError($"{prefix}.ConfirmPin", "Enter the same PIN again to confirm it.");
        }
    }

    private static int? ParseTime(string? value, bool allowEndOfDay)
    {
        if (allowEndOfDay && value == "24:00") return 1440;
        if (value is null || value.Length != 5 || value[2] != ':' ||
            !char.IsAsciiDigit(value[0]) || !char.IsAsciiDigit(value[1]) ||
            !char.IsAsciiDigit(value[3]) || !char.IsAsciiDigit(value[4])) return null;

        var hour = (value[0] - '0') * 10 + value[1] - '0';
        var minute = (value[3] - '0') * 10 + value[4] - '0';
        if (hour > 23 || minute > 59) return null;
        var total = hour * 60 + minute;
        return allowEndOfDay && total == 0 ? null : total;
    }

    public static bool AllowsPin(string mode) => mode is "fingerprintOrPin" or "pinOnly";

    public static string ModeLabel(string mode) => mode switch
    {
        "fingerprintOrPin" => "Fingerprint or PIN",
        "pinOnly" => "PIN only",
        "fingerprintOnly" => "Fingerprint only",
        "denyAll" => "Deny all access",
        _ => "Unknown mode"
    };

    public static string ParityLabel(string parity) => parity switch
    {
        "even" => "Even minutes",
        "odd" => "Odd minutes",
        _ => "Every minute"
    };

    public static string TimeLabel(int minute) => $"{minute / 60:00}:{minute % 60:00}";

    private static CreatePolicyInput DefaultInput() => new()
    {
        AccessMode = "fingerprintOnly", AllowedMinuteParity = "any",
        StartTime = "00:00", EndTime = "24:00", MakeActive = true
    };

    private async Task<PageResult> RedisplayAsync(CancellationToken cancellationToken)
    {
        ClearPinValues();
        await LoadPoliciesAsync(cancellationToken);
        return Page();
    }

    private void ClearPinValues()
    {
        Input.Pin = Input.ConfirmPin = Reset.Pin = Reset.ConfirmPin = string.Empty;
        foreach (var key in new[] { "Input.Pin", "Input.ConfirmPin", "Reset.Pin", "Reset.ConfirmPin" })
        {
            if (ModelState.ContainsKey(key)) ModelState.SetModelValue(key, null, null);
        }
    }

    private async Task LoadPoliciesAsync(CancellationToken cancellationToken)
    {
        try
        {
            Policies = await policyStore.GetAllAsync(cancellationToken);
            PoliciesAvailable = true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not load access policies.");
            ModelState.AddModelError(string.Empty, "Policies could not be loaded. Please try again.");
        }
    }

    public sealed record PolicyPreset(string Id, string Title, string Description, string AccessMode, string Parity, string StartTime, string EndTime);

    public sealed class CreatePolicyInput
    {
        private string policyId = string.Empty;

        [Display(Name = "Policy ID")]
        [Required(ErrorMessage = "Enter a policy ID.")]
        [StringLength(64, ErrorMessage = "Use no more than 64 characters for the policy ID.")]
        [RegularExpression(@"^[A-Z0-9][A-Z0-9_-]{0,63}$", ErrorMessage = "Start with a letter or number and use only letters, numbers, hyphens or underscores.")]
        public string PolicyId
        {
            get => policyId;
            set => policyId = (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        [Display(Name = "Access method")]
        [Required(ErrorMessage = "Choose an access method.")]
        [RegularExpression("^(fingerprintOrPin|pinOnly|fingerprintOnly|denyAll)$", ErrorMessage = "Choose one of the supported access methods.")]
        public string AccessMode { get; set; } = string.Empty;

        [Display(Name = "Allowed minutes")]
        [Required(ErrorMessage = "Choose which minutes to allow.")]
        [RegularExpression("^(any|even|odd)$", ErrorMessage = "Choose every, even or odd minutes.")]
        public string AllowedMinuteParity { get; set; } = string.Empty;

        [Display(Name = "From")]
        public string StartTime { get; set; } = string.Empty;

        [Display(Name = "Until")]
        public string EndTime { get; set; } = string.Empty;

        [Display(Name = "Make this the active policy")]
        public bool MakeActive { get; set; }

        public string? Pin { get; set; }
        public string? ConfirmPin { get; set; }
    }

    public sealed class ResetPinInput
    {
        private string policyId = string.Empty;

        [Required(ErrorMessage = "Choose a policy to update.")]
        [RegularExpression(@"^[A-Z0-9][A-Z0-9_-]{0,63}$", ErrorMessage = "Choose a valid policy to update.")]
        public string PolicyId
        {
            get => policyId;
            set => policyId = (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        public string? Pin { get; set; }
        public string? ConfirmPin { get; set; }
    }
}
