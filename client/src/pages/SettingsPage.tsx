import { useState } from "react";
import { useForm, type AnyFieldApi } from "@tanstack/react-form";
import { toast } from "sonner";
import {
    useGlobalSettings,
    useSaveGlobalSettings,
    useSaveSettings,
    useUserSettings
} from "@/api/queries";
import { SettingsSchema, type GlobalSettings, type Settings } from "@/lib/schemas";
import type { ProviderStatus } from "@/lib/schemas";
import { ProviderCard } from "@/components/provider-card";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
    Field,
    FieldContent,
    FieldDescription,
    FieldError,
    FieldGroup,
    FieldLabel,
    FieldLegend,
    FieldSet
} from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Spinner } from "@/components/ui/spinner";
import { Switch } from "@/components/ui/switch";

type BoolField =
    | "updateNsfw"
    | "enableAutoSync"
    | "syncOnlyCompleted"
    | "setStartDateFromAnyEpisode"
    | "enableRewatchDetection"
    | "allowRollback"
    | "useFuzzyMatching"
    | "enableDebugLogging";

type Toggle = { name: BoolField; label: string; desc: string };

const groups: { legend: string; toggles: Toggle[] }[] = [
    {
        legend: "Sync behavior",
        toggles: [
            {
                name: "enableAutoSync",
                label: "Auto-sync",
                desc: "Sync watch status automatically on playback."
            },
            {
                name: "syncOnlyCompleted",
                label: "Sync only on completion",
                desc: "Only update when you finish the last episode."
            },
            {
                name: "enableRewatchDetection",
                label: "Rewatch detection",
                desc: "Detect rewatches and bump the repeat count."
            },
            {
                name: "allowRollback",
                label: "Allow rollback",
                desc: "Let progress decrease when rewatching earlier episodes."
            },
            {
                name: "setStartDateFromAnyEpisode",
                label: "Start date from any episode",
                desc: "Set the start date on the first watched episode, not only episode 1."
            }
        ]
    },
    {
        legend: "Matching",
        toggles: [
            {
                name: "useFuzzyMatching",
                label: "Fuzzy title matching",
                desc: "Match titles loosely when no exact ID is found."
            }
        ]
    },
    {
        legend: "Advanced",
        toggles: [
            {
                name: "updateNsfw",
                label: "Update NSFW",
                desc: "Include adult titles when syncing."
            },
            {
                name: "enableDebugLogging",
                label: "Debug logging",
                desc: "Verbose logs for troubleshooting."
            }
        ]
    }
];

const NumberInput = ({
    field,
    ...props
}: { field: AnyFieldApi } & React.ComponentProps<typeof Input>) => {
    const [text, setText] = useState(String(field.state.value ?? ""));
    const [lastValue, setLastValue] = useState(field.state.value);
    if (field.state.value !== lastValue) {
        setLastValue(field.state.value);
        setText(String(field.state.value ?? ""));
    }

    return (
        <Input
            {...props}
            type="number"
            value={text}
            onChange={(e) => {
                setText(e.target.value);
                const v = e.target.valueAsNumber;
                if (!Number.isNaN(v)) field.handleChange(v);
            }}
            onBlur={() => {
                if (Number.isNaN(Number.parseFloat(text))) {
                    field.handleChange(0);
                    setText("0");
                }
            }}
        />
    );
};

const sameSettings = (a: Settings, b: Settings) =>
    (Object.keys(a) as (keyof Settings)[]).every((k) => a[k] === b[k]);

const SettingsForm = ({ initial }: { initial: Settings }) => {
    const save = useSaveSettings();
    const [baseline, setBaseline] = useState(initial);
    const form = useForm({
        defaultValues: initial,
        validators: { onChange: SettingsSchema },
        onSubmit: async ({ value }) => {
            await save.mutateAsync(value);
            form.reset(value);
            setBaseline(value);
            toast.success("Settings saved");
        }
    });

    const renderToggle = (t: Toggle) => (
        <form.Field key={t.name} name={t.name}>
            {(field) => (
                <Field orientation="horizontal">
                    <FieldContent>
                        <FieldLabel htmlFor={field.name}>{t.label}</FieldLabel>
                        <FieldDescription>{t.desc}</FieldDescription>
                    </FieldContent>
                    <Switch
                        id={field.name}
                        checked={field.state.value}
                        onCheckedChange={(v) => field.handleChange(v)}
                    />
                </Field>
            )}
        </form.Field>
    );

    return (
        <form
            onSubmit={(e) => {
                e.preventDefault();
                e.stopPropagation();
                void form.handleSubmit();
            }}
        >
            <div className="space-y-8">
                {groups.map((g) => (
                    <FieldSet key={g.legend}>
                        <FieldLegend>{g.legend}</FieldLegend>
                        <FieldGroup>
                            {g.toggles.map(renderToggle)}
                            {g.legend === "Matching" && (
                                <form.Field name="titleMatchThreshold">
                                    {(field) => (
                                        <Field>
                                            <FieldLabel htmlFor={field.name}>
                                                Title match threshold
                                            </FieldLabel>
                                            <FieldDescription>
                                                How strict fuzzy matching is, between 0 and 1.
                                            </FieldDescription>
                                            <NumberInput
                                                field={field}
                                                id={field.name}
                                                step="0.05"
                                                min={0}
                                                max={1}
                                                className="max-w-40"
                                            />
                                            <FieldError errors={field.state.meta.errors} />
                                        </Field>
                                    )}
                                </form.Field>
                            )}
                            {g.legend === "Sync behavior" && (
                                <form.Field name="syncDelaySeconds">
                                    {(field) => (
                                        <Field>
                                            <FieldLabel htmlFor={field.name}>
                                                Sync delay (seconds)
                                            </FieldLabel>
                                            <FieldDescription>
                                                Wait this long after a watch event before syncing.
                                            </FieldDescription>
                                            <NumberInput
                                                field={field}
                                                id={field.name}
                                                min={0}
                                                max={300}
                                                className="max-w-40"
                                            />
                                            <FieldError errors={field.state.meta.errors} />
                                        </Field>
                                    )}
                                </form.Field>
                            )}
                        </FieldGroup>
                    </FieldSet>
                ))}
            </div>

            <form.Subscribe
                selector={(s) => ({
                    canSubmit: s.canSubmit,
                    isSubmitting: s.isSubmitting,
                    values: s.values
                })}
            >
                {({ canSubmit, isSubmitting, values }) => {
                    const dirty = !sameSettings(values, baseline);
                    return (
                        <div className="mt-6 flex items-center gap-3">
                            <Button type="submit" disabled={!canSubmit || !dirty}>
                                {isSubmitting && <Spinner />}
                                Save settings
                            </Button>
                            {dirty && (
                                <span className="text-sm text-muted-foreground">
                                    Unsaved changes
                                </span>
                            )}
                        </div>
                    );
                }}
            </form.Subscribe>
        </form>
    );
};

const ApiConfigForm = () => {
    const { data, isLoading, isError } = useGlobalSettings(true);
    const save = useSaveGlobalSettings();
    const [creds, setCreds] = useState<GlobalSettings | null>(null);

    if (isLoading) return <Skeleton className="h-48 w-full" />;

    if (isError) {
        return (
            <Alert variant="destructive">
                <AlertDescription>Couldn't load API credentials.</AlertDescription>
            </Alert>
        );
    }

    const base: GlobalSettings = data ?? {
        malClientId: "",
        malClientSecret: "",
        aniListClientId: "",
        aniListClientSecret: ""
    };
    const current: GlobalSettings = creds ?? base;
    const set = (patch: Partial<GlobalSettings>) => setCreds({ ...current, ...patch });
    const dirty = (Object.keys(base) as (keyof GlobalSettings)[]).some(
        (k) => current[k] !== base[k]
    );

    const fields: { key: keyof GlobalSettings; label: string; secret?: boolean }[] = [
        { key: "aniListClientId", label: "AniList Client ID" },
        { key: "aniListClientSecret", label: "AniList Client Secret", secret: true },
        { key: "malClientId", label: "MAL Client ID" },
        { key: "malClientSecret", label: "MAL Client Secret", secret: true }
    ];

    return (
        <FieldGroup>
            <div className="grid gap-5 sm:grid-cols-2">
                {fields.map((f) => (
                    <Field key={f.key}>
                        <FieldLabel htmlFor={f.key}>{f.label}</FieldLabel>
                        <Input
                            id={f.key}
                            type={f.secret ? "password" : "text"}
                            value={current[f.key]}
                            onChange={(e) => set({ [f.key]: e.target.value })}
                        />
                    </Field>
                ))}
            </div>
            <Button
                className="w-fit"
                disabled={save.isPending || !dirty}
                onClick={() =>
                    save.mutate(current, {
                        onSuccess: (r) => {
                            setCreds(current);
                            toast.success(
                                r.reAuthRequired
                                    ? "Saved. Changed credentials cleared connections, please reconnect."
                                    : "API credentials saved"
                            );
                        },
                        onError: () => toast.error("Failed to save credentials")
                    })
                }
            >
                {save.isPending && <Spinner />}
                Save API credentials
            </Button>
        </FieldGroup>
    );
};

const SettingsPage = () => {
    const { data, isLoading, isError } = useUserSettings();

    if (isLoading) return <Skeleton className="h-96 w-full" />;

    if (isError || !data) {
        return (
            <Alert variant="destructive">
                <AlertDescription>
                    Log in to Shoko Server to manage your sync settings.
                </AlertDescription>
            </Alert>
        );
    }

    const entries = Object.entries(data.providers) as [string, ProviderStatus][];

    return (
        <div className="space-y-8">
            <Card>
                <CardHeader>
                    <CardTitle>Providers</CardTitle>
                </CardHeader>
                <CardContent className="grid gap-4 sm:grid-cols-2">
                    {entries.map(([key, status]) => (
                        <ProviderCard key={key} providerKey={key} status={status} />
                    ))}
                </CardContent>
            </Card>

            <Card>
                <CardHeader>
                    <CardTitle>Sync settings</CardTitle>
                </CardHeader>
                <CardContent>
                    <SettingsForm initial={data.settings} />
                </CardContent>
            </Card>

            {data.isAdmin && (
                <Card>
                    <CardHeader>
                        <CardTitle>API configuration</CardTitle>
                    </CardHeader>
                    <CardContent>
                        <ApiConfigForm />
                    </CardContent>
                </Card>
            )}
        </div>
    );
};

export default SettingsPage;
