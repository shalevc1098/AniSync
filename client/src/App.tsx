import { useEffect } from "react";
import { NavLink, Route, Routes, useSearchParams } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { LayoutDashboard, Settings2, History as HistoryIcon, User } from "lucide-react";
import { cn } from "@/lib/utils";
import { useWhoami } from "@/api/queries";
import { Brand } from "@/components/brand";
import { ModeToggle } from "@/components/mode-toggle";
import DashboardPage from "@/pages/DashboardPage";
import SettingsPage from "@/pages/SettingsPage";
import HistoryPage from "@/pages/HistoryPage";

const navItems = [
    { to: "/", label: "Dashboard", icon: LayoutDashboard, end: true },
    { to: "/settings", label: "Settings", icon: Settings2, end: false },
    { to: "/history", label: "History", icon: HistoryIcon, end: false }
];

const App = () => {
    const [params, setParams] = useSearchParams();
    const queryClient = useQueryClient();
    const { data: me } = useWhoami();

    const success = params.get("success");
    const error = params.get("error");
    useEffect(() => {
        if (success === "connected") {
            toast.success("Provider connected");
            queryClient.invalidateQueries({ queryKey: ["dashboard"] });
            queryClient.invalidateQueries({ queryKey: ["userSettings"] });
            setParams({}, { replace: true });
        } else if (error) {
            toast.error(decodeURIComponent(error));
            setParams({}, { replace: true });
        }
    }, [success, error, queryClient, setParams]);

    return (
        <div className="min-h-svh bg-background">
            <header className="sticky top-0 z-10 border-b bg-background/80 backdrop-blur">
                <div className="flex h-14 items-center justify-between px-6 lg:px-10">
                    <Brand className="h-8 w-auto text-[#0F1B2D] dark:text-foreground" />
                    <div className="flex items-center gap-3">
                        {me && (
                            <div
                                className="flex items-center gap-2"
                                title={`Shoko user: ${me.Username}`}
                            >
                                {me.Avatar ? (
                                    <img
                                        src={me.Avatar}
                                        alt=""
                                        className="size-7 rounded-full object-cover"
                                    />
                                ) : (
                                    <div className="flex size-7 items-center justify-center rounded-full bg-muted">
                                        <User className="size-4 text-muted-foreground" />
                                    </div>
                                )}
                                <span className="hidden text-sm font-medium sm:inline">
                                    {me.Username}
                                </span>
                            </div>
                        )}
                        <ModeToggle />
                    </div>
                </div>
                <nav className="flex gap-1 px-6 lg:px-10">
                    {navItems.map(({ to, label, icon: Icon, end }) => (
                        <NavLink
                            key={to}
                            to={to}
                            end={end}
                            className={({ isActive }) =>
                                cn(
                                    "-mb-px flex items-center gap-2 border-b-2 px-3 py-2.5 text-sm font-medium transition-colors",
                                    isActive
                                        ? "border-primary text-foreground"
                                        : "border-transparent text-muted-foreground hover:text-foreground"
                                )
                            }
                        >
                            <Icon className="size-4" />
                            {label}
                        </NavLink>
                    ))}
                </nav>
            </header>

            <main className="px-6 py-8 lg:px-10">
                <Routes>
                    <Route path="/" element={<DashboardPage />} />
                    <Route path="/settings" element={<SettingsPage />} />
                    <Route path="/history" element={<HistoryPage />} />
                </Routes>
            </main>
        </div>
    );
};

export default App;
