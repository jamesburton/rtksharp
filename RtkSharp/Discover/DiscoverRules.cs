using System.Text.RegularExpressions;

namespace RtkSharp.Discover;

/// <summary>
/// A single classification rule mapping a raw shell command pattern to its rtk equivalent, plus the
/// metadata <c>rtk discover</c> needs to estimate and report savings. Faithful port of the Rust
/// <c>RtkRule</c> struct (<c>src/discover/rules.rs</c>:3-11).
/// </summary>
/// <param name="Pattern">The regex source string, transcribed verbatim from <c>rules.rs</c>.</param>
/// <param name="CompiledPattern">The compiled form of <paramref name="Pattern"/>, built once at table-construction time.</param>
/// <param name="RtkCmd">The rtk-wrapped command this rule maps to, e.g. <c>"rtk git"</c>.</param>
/// <param name="RewritePrefixes">The raw command prefixes this rule recognizes (unused by discover's classification itself, kept for parity/completeness with the Rust struct).</param>
/// <param name="Category">The category this rule belongs to, e.g. <c>"Git"</c>, used both for display and by <see cref="DiscoverRegistry.CategoryAvgTokens"/>.</param>
/// <param name="SavingsPct">The default estimated token-savings percentage for commands matched by this rule.</param>
/// <param name="SubcmdSavings">Per-subcommand savings percentage overrides (subcommand name → percentage), checked against the rule's first capture group.</param>
/// <param name="SubcmdStatus">Per-subcommand <see cref="RtkStatus"/> overrides (subcommand name → status), checked against the rule's first capture group.</param>
public sealed record DiscoverRule(
    string Pattern,
    Regex CompiledPattern,
    string RtkCmd,
    IReadOnlyList<string> RewritePrefixes,
    string Category,
    double SavingsPct,
    IReadOnlyList<(string Subcmd, double Pct)> SubcmdSavings,
    IReadOnlyList<(string Subcmd, RtkStatus Status)> SubcmdStatus);

/// <summary>
/// The full table of <c>rtk discover</c> classification rules, transcribed verbatim (in source
/// order — order matters, since matching is last-match-wins, mirroring Rust's <c>RegexSet</c>
/// reporting the highest-index match) from <c>src/discover/rules.rs</c>'s <c>RULES</c> const array,
/// plus the <c>IGNORED_EXACT</c>/<c>IGNORED_PREFIXES</c> tables.
/// </summary>
/// <remarks>
/// <b>Deliberate duplication of <see cref="RtkSharp.Rewrite.RewriteRules"/>.</b> Rust's
/// <c>discover::registry::classify_command</c> and <c>discover::registry::rewrite_command</c> share
/// the exact same <c>RULES</c> table (<c>rules.rs</c>) and are not independent schemes. RtkSharp
/// already ported that table once as <see cref="RtkSharp.Rewrite.RewriteRules"/> for the
/// already-completed rewrite-engine port. This port intentionally builds its own copy — with the
/// added <see cref="DiscoverRule.SubcmdSavings"/>/<see cref="DiscoverRule.SubcmdStatus"/> fields that
/// <c>RewriteRule</c> lacks (the rewrite path never needed them) — instead of extending
/// <c>RewriteRule</c> or otherwise touching anything under <c>RtkSharp/Rewrite/</c>, because that
/// directory is a parallel worktree's territory during this port. The tradeoff is a second
/// hand-maintained copy of ~75 regex patterns that must be kept in sync with <c>rules.rs</c> by hand;
/// this is accepted as the cost of avoiding cross-worktree file conflicts.
/// </remarks>
public static class DiscoverRules
{
    /// <summary>
    /// All 75 classification rules, in the same order as <c>src/discover/rules.rs</c>.
    /// </summary>
    public static IReadOnlyList<DiscoverRule> All { get; } = BuildRules();

    private static DiscoverRule R(
        string pattern,
        string rtkCmd,
        string[] rewritePrefixes,
        string category,
        double savingsPct,
        (string, double)[]? subcmdSavings = null,
        (string, RtkStatus)[]? subcmdStatus = null) =>
        new(
            pattern,
            new Regex(pattern, RegexOptions.Compiled),
            rtkCmd,
            rewritePrefixes,
            category,
            savingsPct,
            subcmdSavings ?? [],
            subcmdStatus ?? []);

    private static IReadOnlyList<DiscoverRule> BuildRules() =>
    [
        R(
            @"^(?:git|yadm)\s+(?:-[Cc]\s+\S+\s+)*(status|log|diff|show|add|commit|push|pull|branch|fetch|stash|worktree)",
            "rtk git",
            ["git", "yadm"],
            "Git",
            70.0,
            subcmdSavings:
            [
                ("diff", 80.0),
                ("show", 80.0),
                ("add", 59.0),
                ("commit", 59.0),
            ]),
        R(
            @"^gh\s+(pr|issue|run|repo|api|release)",
            "rtk gh",
            ["gh"],
            "GitHub",
            82.0,
            subcmdSavings: [("pr", 87.0), ("run", 82.0), ("issue", 80.0)]),
        R(
            @"^glab\s+(mr|issue|ci|pipeline|api|release)",
            "rtk glab",
            ["glab"],
            "GitLab",
            82.0,
            subcmdSavings: [("mr", 87.0), ("ci", 82.0), ("issue", 80.0)]),
        R(
            @"^cargo\s+(build|test|clippy|check|fmt|install)",
            "rtk cargo",
            ["cargo"],
            "Cargo",
            80.0,
            subcmdSavings: [("test", 90.0), ("check", 80.0)],
            subcmdStatus: [("fmt", RtkStatus.Passthrough)]),
        R(
            @"^pnpm\s+(exec|i|install|list|ls|outdated|run|run-script)",
            "rtk pnpm",
            ["pnpm"],
            "PackageManager",
            80.0),
        R(
            @"^npm\s+(exec|run|run-script|rum|urn|x)(\s|$)",
            "rtk npm",
            ["npm"],
            "PackageManager",
            70.0),
        R(
            @"^npx\s+",
            "rtk npx",
            ["npx"],
            "PackageManager",
            70.0),
        R(
            @"^(cat|head|tail)\s+",
            "rtk read",
            ["cat", "head", "tail"],
            "Files",
            60.0),
        R(
            @"^(rg|grep)\s+",
            "rtk grep",
            ["rg", "grep"],
            "Files",
            75.0),
        R(
            @"^ls(\s|$)",
            "rtk ls",
            ["ls"],
            "Files",
            65.0),
        R(
            @"^find\s+",
            "rtk find",
            ["find"],
            "Files",
            70.0),
        R(
            @"^((p?np(m|x)|p?npm\s+(exec|run|run-script)|npm\s+(rum|urn|x)|pnpm\s+dlx)\s+)?tsc(\s|$)",
            "rtk tsc",
            [
                "npm exec tsc",
                "npm rum tsc",
                "npm run tsc",
                "npm run-script tsc",
                "npm tsc",
                "npm urn tsc",
                "npm x tsc",
                "npx tsc",
                "pnpm dlx tsc",
                "pnpm exec tsc",
                "pnpm run tsc",
                "pnpm run-script tsc",
                "pnpm tsc",
                "pnpx tsc",
                "tsc",
            ],
            "Build",
            83.0),
        R(
            @"^((p?np(m|x)|p?npm\s+(exec|run|run-script)|npm\s+(rum|urn|x)|pnpm\s+dlx)\s+)?(biome|eslint|lint)(\s|$)",
            "rtk lint",
            [
                "biome",
                "eslint",
                "lint",
                "npm biome",
                "npm eslint",
                "npm exec biome",
                "npm exec eslint",
                "npm lint",
                "npm rum biome",
                "npm rum eslint",
                "npm rum lint",
                "npm run biome",
                "npm run eslint",
                "npm run lint",
                "npm run-script biome",
                "npm run-script eslint",
                "npm run-script lint",
                "npm urn biome",
                "npm urn eslint",
                "npm urn lint",
                "npm x biome",
                "npm x eslint",
                "npx biome",
                "npx eslint",
                "npx lint",
                "pnpm biome",
                "pnpm dlx biome",
                "pnpm dlx eslint",
                "pnpm eslint",
                "pnpm exec biome",
                "pnpm exec eslint",
                "pnpm lint",
                "pnpm run biome",
                "pnpm run eslint",
                "pnpm run lint",
                "pnpm run-script biome",
                "pnpm run-script eslint",
                "pnpm run-script lint",
                "pnpx biome",
                "pnpx eslint",
                "pnpx lint",
            ],
            "Build",
            84.0),
        R(
            @"^((p?np(m|x)|p?npm\s+(exec|run|run-script)|npm\s+(rum|urn|x)|pnpm\s+dlx)\s+)?prettier",
            "rtk prettier",
            [
                "npm exec prettier",
                "npm prettier",
                "npm rum prettier",
                "npm run prettier",
                "npm run-script prettier",
                "npm urn prettier",
                "npm x prettier",
                "npx prettier",
                "pnpm dlx prettier",
                "pnpm exec prettier",
                "pnpm prettier",
                "pnpm run prettier",
                "pnpm run-script prettier",
                "pnpx prettier",
                "prettier",
            ],
            "Build",
            70.0),
        R(
            @"^((p?np(m|x)|p?npm\s+(exec|run|run-script)|npm\s+(rum|urn|x)|pnpm\s+dlx)\s+)?next\s+build",
            "rtk next",
            [
                "next build",
                "npm exec next build",
                "npm next build",
                "npm rum next build",
                "npm run next build",
                "npm run-script next build",
                "npm urn next build",
                "npm x next build",
                "npx next build",
                "pnpm dlx next build",
                "pnpm exec next build",
                "pnpm next build",
                "pnpm run next build",
                "pnpm run-script next build",
                "pnpx next build",
            ],
            "Build",
            87.0),
        R(
            @"^((p?np(m|x)|p?npm\s+(exec|run|run-script)|npm\s+(rum|urn|x)|pnpm\s+dlx)\s+)?jest(\s+run)?(\s|$)",
            "rtk jest",
            [
                "jest run",
                "jest",
                "npm exec jest run",
                "npm exec jest",
                "npm jest run",
                "npm jest",
                "npm rum jest run",
                "npm rum jest",
                "npm run jest run",
                "npm run jest",
                "npm run-script jest run",
                "npm run-script jest",
                "npm urn jest run",
                "npm urn jest",
                "npm x jest run",
                "npm x jest",
                "npx jest run",
                "npx jest",
                "pnpm dlx jest run",
                "pnpm dlx jest",
                "pnpm exec jest run",
                "pnpm exec jest",
                "pnpm jest run",
                "pnpm jest",
                "pnpm run jest run",
                "pnpm run jest",
                "pnpm run-script jest run",
                "pnpm run-script jest",
                "pnpx jest run",
                "pnpx jest",
            ],
            "Tests",
            99.0),
        R(
            @"^((p?np(m|x)|p?npm\s+(exec|run|run-script)|npm\s+(rum|urn|x)|pnpm\s+dlx)\s+)?vitest(\s+run)?(\s|$)",
            "rtk vitest",
            [
                "npm exec vitest run",
                "npm exec vitest",
                "npm rum vitest run",
                "npm rum vitest",
                "npm run vitest run",
                "npm run vitest",
                "npm run-script vitest run",
                "npm run-script vitest",
                "npm urn vitest run",
                "npm urn vitest",
                "npm vitest run",
                "npm vitest",
                "npm x vitest run",
                "npm x vitest",
                "npx vitest run",
                "npx vitest",
                "pnpm dlx vitest run",
                "pnpm dlx vitest",
                "pnpm exec vitest run",
                "pnpm exec vitest",
                "pnpm run vitest run",
                "pnpm run vitest",
                "pnpm run-script vitest run",
                "pnpm run-script vitest",
                "pnpm vitest run",
                "pnpm vitest",
                "pnpx vitest run",
                "pnpx vitest",
                "vitest run",
                "vitest",
            ],
            "Tests",
            99.0),
        R(
            @"^((p?np(m|x)|p?npm\s+(exec|run|run-script)|npm\s+(rum|urn|x)|pnpm\s+dlx)\s+)?playwright",
            "rtk playwright",
            [
                "npm exec playwright",
                "npm playwright",
                "npm rum playwright",
                "npm run playwright",
                "npm run-script playwright",
                "npm urn playwright",
                "npm x playwright",
                "npx playwright",
                "playwright",
                "pnpm dlx playwright",
                "pnpm exec playwright",
                "pnpm playwright",
                "pnpm run playwright",
                "pnpm run-script playwright",
                "pnpx playwright",
            ],
            "Tests",
            94.0),
        R(
            @"^((p?np(m|x)|p?npm\s+(exec|run|run-script)|npm\s+(rum|urn|x)|pnpm\s+dlx)\s+)?prisma",
            "rtk prisma",
            [
                "npm exec prisma",
                "npm prisma",
                "npm rum prisma",
                "npm run prisma",
                "npm run-script prisma",
                "npm urn prisma",
                "npm x prisma",
                "npx prisma",
                "pnpm dlx prisma",
                "pnpm exec prisma",
                "pnpm prisma",
                "pnpm run prisma",
                "pnpm run-script prisma",
                "pnpx prisma",
                "prisma",
            ],
            "Build",
            88.0),
        R(
            @"^docker\s+(ps|images|logs|run|exec|build|compose\s+(ps|logs|build))",
            "rtk docker",
            ["docker"],
            "Infra",
            85.0),
        R(
            @"^kubectl\s+(get|logs|describe|apply)",
            "rtk kubectl",
            ["kubectl"],
            "Infra",
            85.0),
        R(
            @"^oc\s+(get|logs|describe|apply|status|adm)",
            "rtk oc",
            ["oc"],
            "Infra",
            85.0),
        R(
            @"^tree(\s|$)",
            "rtk tree",
            ["tree"],
            "Files",
            70.0),
        R(
            @"^diff\s+",
            "rtk diff",
            ["diff"],
            "Files",
            60.0),
        R(
            @"^curl\s+",
            "rtk curl",
            ["curl"],
            "Network",
            70.0),
        R(
            @"^wget\s+",
            "rtk wget",
            ["wget"],
            "Network",
            65.0),
        R(
            @"^(python3?\s+-m\s+)?mypy(\s|$)",
            "rtk mypy",
            ["python3 -m mypy", "python -m mypy", "mypy"],
            "Build",
            80.0),
        R(
            @"^ruff\s+(check|format)",
            "rtk ruff",
            ["ruff"],
            "Python",
            80.0,
            subcmdSavings: [("check", 80.0), ("format", 75.0)]),
        R(
            @"^(python[0-9.]*\s+-m\s+)?pytest(\s|$)",
            "rtk pytest",
            ["python3 -m pytest", "python -m pytest", "pytest"],
            "Python",
            90.0),
        R(
            @"^(pip3?|uv\s+pip)\s+(list|outdated|install|show)",
            "rtk pip",
            ["pip3", "pip", "uv pip"],
            "Python",
            75.0,
            subcmdSavings: [("list", 75.0), ("outdated", 80.0)]),
        R(
            @"^go\s+(test|build|vet)",
            "rtk go",
            ["go"],
            "Go",
            85.0,
            subcmdSavings: [("test", 90.0), ("build", 80.0), ("vet", 75.0)]),
        R(
            @"^(?:golangci-lint|golangci)\s+(run)(?:\s|$)",
            "rtk golangci-lint run",
            ["golangci-lint run", "golangci run"],
            "Go",
            85.0),
        R(
            @"^bundle\s+(install|update)\b",
            "rtk bundle",
            ["bundle"],
            "Ruby",
            70.0),
        R(
            @"^(?:bundle\s+exec\s+)?(?:bin/)?(?:rake|rails)\s+test",
            "rtk rake",
            ["bundle exec rails", "bundle exec rake", "bin/rails", "rails", "rake"],
            "Ruby",
            85.0,
            subcmdSavings: [("test", 90.0)]),
        R(
            @"^(?:bundle\s+exec\s+)?rspec(?:\s|$)",
            "rtk rspec",
            ["bundle exec rspec", "bin/rspec", "rspec"],
            "Tests",
            65.0),
        R(
            @"^(?:bundle\s+exec\s+)?rubocop(?:\s|$)",
            "rtk rubocop",
            ["bundle exec rubocop", "rubocop"],
            "Build",
            65.0),
        R(
            @"^aws\s+",
            "rtk aws",
            ["aws"],
            "Infra",
            80.0,
            subcmdSavings:
            [
                ("sts", 80.0),
                ("s3", 60.0),
                ("ec2", 85.0),
                ("ecs", 90.0),
                ("rds", 80.0),
                ("cloudformation", 90.0),
                ("logs", 88.0),
                ("lambda", 90.0),
                ("iam", 85.0),
                ("dynamodb", 70.0),
                ("s3api", 75.0),
                ("eks", 87.0),
                ("sqs", 78.0),
                ("secretsmanager", 75.0),
            ]),
        R(
            @"^psql(\s|$)",
            "rtk psql",
            ["psql"],
            "Infra",
            75.0),
        R(
            @"^ansible-playbook\b",
            "rtk ansible-playbook",
            ["ansible-playbook"],
            "Infra",
            70.0),
        R(
            @"^brew\s+(install|upgrade)\b",
            "rtk brew",
            ["brew"],
            "PackageManager",
            65.0),
        R(
            @"^composer\s+(install|update|require)\b",
            "rtk composer",
            ["composer"],
            "PackageManager",
            65.0),
        R(
            @"^df(\s|$)",
            "rtk df",
            ["df"],
            "System",
            60.0),
        R(
            @"^dotnet\s+build\b",
            "rtk dotnet",
            ["dotnet"],
            "Build",
            70.0),
        R(
            @"^du\b",
            "rtk du",
            ["du"],
            "System",
            60.0),
        R(
            @"^fail2ban-client\b",
            "rtk fail2ban-client",
            ["fail2ban-client"],
            "Infra",
            60.0),
        R(
            @"^gcloud\b",
            "rtk gcloud",
            ["gcloud"],
            "Infra",
            65.0),
        R(
            @"^(?:\./gradlew|gradlew\.bat|gradlew|gradle)(?:\s+(test|build|clean|assemble\w*|install\w*|check|lint\w*|dependencies))?(\s|$)",
            "rtk gradlew",
            ["./gradlew", "gradlew.bat", "gradlew", "gradle"],
            "Build",
            75.0,
            subcmdSavings: [("test", 90.0), ("build", 80.0), ("check", 80.0)]),
        R(
            @"^hadolint\b",
            "rtk hadolint",
            ["hadolint"],
            "Build",
            65.0),
        R(
            @"^helm\b",
            "rtk helm",
            ["helm"],
            "Infra",
            65.0),
        R(
            @"^iptables\b",
            "rtk iptables",
            ["iptables"],
            "Infra",
            60.0),
        R(
            @"^make\b",
            "rtk make",
            ["make"],
            "Build",
            65.0),
        R(
            @"^markdownlint\b",
            "rtk markdownlint",
            ["markdownlint"],
            "Build",
            65.0),
        R(
            @"^mix\s+(compile|format)(\s|$)",
            "rtk mix",
            ["mix"],
            "Build",
            65.0),
        R(
            @"^(?:\./mvnw|mvnw\.cmd|mvnw|mvn)\b(?:\s+\S+)*?\s+(compile|test|integration-test|package|install|verify|deploy)\b",
            "rtk mvn",
            ["./mvnw", "mvnw.cmd", "mvnw", "mvn"],
            "Build",
            82.0),
        R(
            @"^ping\b",
            "rtk ping",
            ["ping"],
            "Network",
            60.0),
        R(
            @"^pio\s+run",
            "rtk pio",
            ["pio"],
            "Build",
            65.0),
        R(
            @"^poetry\s+(install|lock|update)\b",
            "rtk poetry",
            ["poetry"],
            "Python",
            65.0),
        R(
            @"^pre-commit\b",
            "rtk pre-commit",
            ["pre-commit"],
            "Build",
            65.0),
        R(
            @"^ps(\s|$)",
            "rtk ps",
            ["ps"],
            "System",
            60.0),
        R(
            @"^pulumi\s+(preview|up|destroy|refresh|stack)(\s|$)",
            "rtk pulumi",
            ["pulumi"],
            "Infra",
            45.0,
            subcmdSavings:
            [
                ("up", 66.0),
                ("destroy", 72.0),
                ("refresh", 35.0),
                ("preview", 25.0),
                ("stack", 29.0),
            ]),
        R(
            @"^quarto\s+render",
            "rtk quarto",
            ["quarto"],
            "Build",
            65.0),
        R(
            @"^rsync\b",
            "rtk rsync",
            ["rsync"],
            "Network",
            65.0),
        R(
            @"^shellcheck\b",
            "rtk shellcheck",
            ["shellcheck"],
            "Build",
            65.0),
        R(
            @"^shopify\s+theme\s+(push|pull)",
            "rtk shopify",
            ["shopify"],
            "Build",
            65.0),
        R(
            @"^sops\b",
            "rtk sops",
            ["sops"],
            "Infra",
            60.0),
        R(
            @"^swift\s+(build|test)\b",
            "rtk swift",
            ["swift"],
            "Build",
            65.0,
            subcmdSavings: [("test", 90.0)]),
        R(
            @"^systemctl\s+status\b",
            "rtk systemctl",
            ["systemctl"],
            "System",
            65.0),
        R(
            @"^terraform\s+plan",
            "rtk terraform",
            ["terraform"],
            "Infra",
            70.0),
        R(
            @"^tofu\s+(fmt|init|plan|validate)(\s|$)",
            "rtk tofu",
            ["tofu"],
            "Infra",
            70.0),
        R(
            @"^trunk\s+build",
            "rtk trunk",
            ["trunk"],
            "Build",
            65.0),
        R(
            @"^uv\s+(sync|pip\s+install)\b",
            "rtk uv",
            ["uv"],
            "Python",
            65.0),
        R(
            @"^yamllint\b",
            "rtk yamllint",
            ["yamllint"],
            "Build",
            65.0),
        R(
            @"^wc(\s|$)",
            "rtk wc",
            ["wc"],
            "Files",
            60.0),
        R(
            @"^gt\s+",
            "rtk gt",
            ["gt"],
            "Git",
            70.0),
        R(
            @"^liquibase(?:\s|$)",
            "rtk liquibase",
            ["liquibase"],
            "Infra",
            65.0),
    ];

    /// <summary>
    /// Command prefixes that always classify as <see cref="Classification.Ignored"/>. Transcribed
    /// verbatim from Rust <c>IGNORED_PREFIXES</c> (<c>rules.rs</c>:907-955).
    /// </summary>
    public static readonly IReadOnlyList<string> IgnoredPrefixes =
    [
        "cd ", "cd\t", "echo ", "printf ", "export ", "source ", "mkdir ", "rm ", "mv ", "cp ",
        "chmod ", "chown ", "touch ", "which ", "type ", "test ", "true", "false", "sleep ",
        "wait", "kill ", "set ", "unset ", "sort ", "uniq ", "tr ", "cut ", "awk ", "sed ",
        "python3 -c", "python -c", "node -e", "ruby -e", "rtk ", "pwd", "bash ", "sh ",
        "then\n", "then ", "else\n", "else ", "do\n", "do ", "for ", "while ", "if ", "case ",
    ];

    /// <summary>
    /// Exact commands that always classify as <see cref="Classification.Ignored"/>. Transcribed
    /// verbatim from Rust <c>IGNORED_EXACT</c> (<c>rules.rs</c>:957-959).
    /// </summary>
    public static readonly IReadOnlyList<string> IgnoredExact =
        ["cd", "echo", "true", "false", "wait", "pwd", "bash", "sh", "fi", "done"];
}
