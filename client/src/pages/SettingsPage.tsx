import { useState } from "react";
import { useForm } from "@tanstack/react-form";
import { toast } from "sonner";
import {
    useDisconnect,
    useGlobalSettings,
    useSaveGlobalSettings,
    useSaveSettings,
    useUserSettings
} from "@/api/queries";
import { SettingsSchema, type GlobalSettings, type Settings } from "@/lib/schemas";
import type { ProviderStatus } from "@/lib/schemas";
import { PROVIDER_API, PROVIDER_LABELS } from "@/lib/format";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import {
    Field,
    FieldContent,
    FieldDescription,
    FieldError,
    FieldGroup,
    FieldLabel
} from "@/components/ui/field";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Spinner } from "@/components/ui/spinner";
import { Switch } from "@/components/ui/switch";
import {
    AlertDialog,
    AlertDialogAction,
    AlertDialogCancel,
    AlertDialogContent,
    AlertDialogDescription,
    AlertDialogFooter,
    AlertDialogHeader,
    AlertDialogTitle,
    AlertDialogTrigger
} from "@/components/ui/alert-dialog";

type BoolField =
    | "updateNsfw"
    | "enableAutoSync"
    | "syncOnlyCompleted"
    | "setStartDateFromAnyEpisode"
    | "enableRewatchDetection"
    | "allowRollback"
    | "useFuzzyMatching"
    | "enableDebugLogging";

const toggles: { name: BoolField; label: string; desc: string }[] = [
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
    { name: "updateNsfw", label: "Update NSFW", desc: "Include adult titles when syncing." },
    {
        name: "setStartDateFromAnyEpisode",
        label: "Start date from any episode",
        desc: "Set the start date on the first watched episode, not only episode 1."
    },
    {
        name: "allowRollback",
        label: "Allow rollback",
        desc: "Let progress decrease when rewatching earlier episodes."
    },
    {
        name: "useFuzzyMatching",
        label: "Fuzzy title matching",
        desc: "Match titles loosely when no exact ID is found."
    },
    {
        name: "enableDebugLogging",
        label: "Debug logging",
        desc: "Verbose logs for troubleshooting."
    }
];

const SettingsForm = ({ initial }: { initial: Settings }) => {
    const save = useSaveSettings();
    const form = useForm({
        defaultValues: initial,
        validators: { onChange: SettingsSchema },
        onSubmit: async ({ value }) => {
            await save.mutateAsync(value);
            toast.success("Settings saved");
        }
    });

    return (
        <form
            onSubmit={(e) => {
                e.preventDefault();
                e.stopPropagation();
                void form.handleSubmit();
            }}
        >
            <FieldGroup>
                {toggles.map((t) => (
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
                ))}

                <form.Field name="titleMatchThreshold">
                    {(field) => (
                        <Field>
                            <FieldLabel htmlFor={field.name}>Title match threshold</FieldLabel>
                            <FieldDescription>
                                How strict fuzzy matching is, between 0 and 1.
                            </FieldDescription>
                            <Input
                                id={field.name}
                                type="number"
                                step="0.05"
                                min={0}
                                max={1}
                                value={field.state.value}
                                onChange={(e) => field.handleChange(e.target.valueAsNumber)}
                            />
                            <FieldError errors={field.state.meta.errors} />
                        </Field>
                    )}
                </form.Field>

                <form.Field name="syncDelaySeconds">
                    {(field) => (
                        <Field>
                            <FieldLabel htmlFor={field.name}>Sync delay (seconds)</FieldLabel>
                            <FieldDescription>
                                Wait this long after a watch event before syncing.
                            </FieldDescription>
                            <Input
                                id={field.name}
                                type="number"
                                min={0}
                                max={300}
                                value={field.state.value}
                                onChange={(e) => field.handleChange(e.target.valueAsNumber)}
                            />
                            <FieldError errors={field.state.meta.errors} />
                        </Field>
                    )}
                </form.Field>

                <form.Subscribe selector={(s) => [s.canSubmit, s.isSubmitting] as const}>
                    {([canSubmit, isSubmitting]) => (
                        <Button type="submit" disabled={!canSubmit} className="w-fit">
                            {isSubmitting && <Spinner />}
                            Save settings
                        </Button>
                    )}
                </form.Subscribe>
            </FieldGroup>
        </form>
    );
};

const ProvidersSection = ({ providers }: { providers: Record<string, ProviderStatus> }) => {
    const disconnect = useDisconnect();
    const entries = Object.entries(providers) as [string, ProviderStatus][];

    return (
        <div className="space-y-3">
            {entries.map(([key, p]) => (
                <div key={key} className="flex items-center gap-3">
                    <span className="w-28 font-medium">{PROVIDER_LABELS[key]}</span>
                    <Badge variant={p.connected ? "default" : "secondary"}>
                        {p.connected ? "Connected" : "Not connected"}
                    </Badge>
                    {p.connected && (
                        <>
                            <span className="text-sm text-primary">{p.username}</span>
                            <AlertDialog>
                                <AlertDialogTrigger asChild>
                                    <Button variant="destructive" size="sm">
                                        Disconnect
                                    </Button>
                                </AlertDialogTrigger>
                                <AlertDialogContent>
                                    <AlertDialogHeader>
                                        <AlertDialogTitle>
                                            Disconnect {PROVIDER_LABELS[key]}?
                                        </AlertDialogTitle>
                                        <AlertDialogDescription>
                                            This removes the stored token. You can reconnect from
                                            the dashboard at any time.
                                        </AlertDialogDescription>
                                    </AlertDialogHeader>
                                    <AlertDialogFooter>
                                        <AlertDialogCancel>Cancel</AlertDialogCancel>
                                        <AlertDialogAction
                                            onClick={() =>
                                                disconnect.mutate(PROVIDER_API[key], {
                                                    onSuccess: () =>
                                                        toast.success(
                                                            `Disconnected ${PROVIDER_LABELS[key]}`
                                                        )
                                                })
                                            }
                                        >
                                            Disconnect
                                        </AlertDialogAction>
                                    </AlertDialogFooter>
                                </AlertDialogContent>
                            </AlertDialog>
                        </>
                    )}
                </div>
            ))}
        </div>
    );
};

const ApiConfigForm = () => {
    const { data, isLoading } = useGlobalSettings(true);
    const save = useSaveGlobalSettings();
    const [creds, setCreds] = useState<GlobalSettings | null>(null);

    if (isLoading) return <Skeleton className="h-48 w-full" />;

    const current: GlobalSettings = creds ??
        data ?? {
            malClientId: "",
            malClientSecret: "",
            aniListClientId: "",
            aniListClientSecret: ""
        };

    const set = (patch: Partial<GlobalSettings>) => setCreds({ ...current, ...patch });

    const fields: { key: keyof GlobalSettings; label: string; secret?: boolean }[] = [
        { key: "aniListClientId", label: "AniList Client ID" },
        { key: "aniListClientSecret", label: "AniList Client Secret", secret: true },
        { key: "malClientId", label: "MAL Client ID" },
        { key: "malClientSecret", label: "MAL Client Secret", secret: true }
    ];

    return (
        <FieldGroup>
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
            <Button
                className="w-fit"
                disabled={save.isPending}
                onClick={() =>
                    save.mutate(current, {
                        onSuccess: (r) =>
                            toast.success(
                                r.reAuthRequired
                                    ? "Saved. Changed credentials cleared connections, please reconnect."
                                    : "API credentials saved"
                            ),
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

    return (
        <div className="space-y-8">
            <Card>
                <CardHeader>
                    <CardTitle>Sync settings</CardTitle>
                </CardHeader>
                <CardContent>
                    <SettingsForm initial={data.settings} />
                </CardContent>
            </Card>

            <Card>
                <CardHeader>
                    <CardTitle>Providers</CardTitle>
                </CardHeader>
                <CardContent>
                    <p className="mb-4 text-sm text-muted-foreground">
                        Watch events sync to every connected provider.
                    </p>
                    <ProvidersSection providers={data.providers} />
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
