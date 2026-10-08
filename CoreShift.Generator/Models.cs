namespace CoreShift.Generator;

// The legacy core's tables, laid out the way its nightly extract writes them.
// Like most old cores it keeps dates as text (in two formats), flags as Y/N and codes
// as short strings, and every product shares one wide ACCOUNTS table, so most columns
// are blank on most rows.

public record LegacyCustomer
{
    public string CustId { get; set; } = "";
    public string Title { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = "";
    public string Gender { get; set; } = "";
    public string DateOfBirth { get; set; } = "";
    public string AddressLine { get; set; } = "";
    public string Suburb { get; set; } = "";
    public string State { get; set; } = "";
    public string Postcode { get; set; } = "";
    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public string PreferredContact { get; set; } = "";
    public string CustomerSince { get; set; } = "";
    public string HomeBranch { get; set; } = "";
    public string EmploymentStatus { get; set; } = "";
    public string? Occupation { get; set; }
    public string? Employer { get; set; }
    public decimal AnnualIncome { get; set; }
    public string ResidentialStatus { get; set; } = "";
    public string MaritalStatus { get; set; } = "";
    public int Dependants { get; set; }
    public string TfnStatus { get; set; } = "";         // PROVIDED, NOT_PROVIDED, EXEMPT, NON_RESIDENT - never the TFN itself
    public string TaxResidency { get; set; } = "";
    public string KycStatus { get; set; } = "";         // VERIFIED, PENDING, PRE_COMMENCEMENT (joined before AML/CTF ID rules)
    public string? KycIdType { get; set; }              // document type only; no ID numbers are generated
    public string? KycVerifiedDate { get; set; }
    public string AmlRiskRating { get; set; } = "";
    public bool Pep { get; set; }                       // politically exposed person
    // From the bureau check at the customer's latest credit application; blank if never checked
    public int? CreditScore { get; set; }               // 0-1200
    public string? CreditScoreDate { get; set; }
    public int? CreditEnquiries5y { get; set; }
    public int? CreditDefaults { get; set; }
    public bool Bankrupt { get; set; }
    public bool Vulnerable { get; set; }
    public bool Deceased { get; set; }
    public bool MarketingConsent { get; set; }
}

public record LegacyAccount
{
    public string AcctNo { get; set; } = "";
    public string Bsb { get; set; } = "";
    public string ProductCode { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string Branch { get; set; } = "";
    public string OpenDate { get; set; } = "";
    public string? CloseDate { get; set; }
    public string Status { get; set; } = "";            // OPEN, CLOSED, DORMANT, FROZEN
    // Customer's point of view: positive = money the bank holds for them, negative = money they owe
    public decimal Balance { get; set; }
    public decimal AvailableBalance { get; set; }
    public decimal InterestRate { get; set; }           // % p.a.
    public decimal AccruedInterest { get; set; }        // earned or charged since the last posting, same sign rule as Balance
    public string? LastInterestDate { get; set; }
    public decimal OverdraftLimit { get; set; }
    public decimal? CreditLimit { get; set; }
    public string? MaskedCardNo { get; set; }           // full card numbers never leave the card system
    // Loans and term deposits
    public decimal? OriginalAmount { get; set; }
    public int? TermMonths { get; set; }
    public string? MaturityDate { get; set; }
    public string? MaturityInstruction { get; set; }
    public decimal? RepaymentAmount { get; set; }       // credit cards: minimum payment due
    public string? RepaymentFrequency { get; set; }
    public string? RepaymentType { get; set; }          // PI = principal and interest, IO = interest only
    public string? NextDueDate { get; set; }
    public int? ArrearsDays { get; set; }
    public decimal? ArrearsAmount { get; set; }
    // Last 24 months, oldest first: 0 on time, 1 = 1-29 days overdue, 2 = 30-59 ... 6 = 150-179, X = 180+, - = not open
    public string? RepaymentHistory24m { get; set; }
    public bool Hardship { get; set; }
    public string? LoanPurpose { get; set; }
    public decimal? SecurityValue { get; set; }
    public decimal? OriginalLvr { get; set; }
    public string? FixedRateExpiry { get; set; }
    public string? LinkedAcctNo { get; set; }           // offset account <-> home loan
    public string? LastActivityDate { get; set; }       // last customer-initiated transaction
}

// Who owns which account. Joint accounts have one PRIMARY and one JOINT row.
public record LegacyAccountHolder
{
    public string AcctNo { get; set; } = "";
    public string CustId { get; set; } = "";
    public string Role { get; set; } = "";
}

public record LegacyTransaction
{
    public string TxnId { get; set; } = "";
    public string AcctNo { get; set; } = "";
    public string TxnDate { get; set; } = "";           // when the customer did it
    public string? PostDate { get; set; }               // when it hit the balance; blank while pending
    public string TxnCode { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal? RunningBalance { get; set; }
    public string Status { get; set; } = "";            // POSTED or PENDING
    public string Channel { get; set; } = "";
}
