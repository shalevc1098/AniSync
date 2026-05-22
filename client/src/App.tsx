import { NavLink, Route, Routes } from "react-router-dom";
import { LayoutDashboard, Settings2, History as HistoryIcon, RefreshCw } from "lucide-react";
import { cn } from "@/lib/utils";
import { ModeToggle } from "@/components/mode-toggle";
import DashboardPage from "@/pages/DashboardPage";
import SettingsPage from "@/pages/SettingsPage";
import HistoryPage from "@/pages/HistoryPage";

const navItems = [
    { to: "/", label: "Dashboard", icon: LayoutDashboard, end: true },
    { to: "/settings", label: "Settings", icon: Settings2, end: false },
    { to: "/history", label: "History", icon: HistoryIcon, end: false }
];

const App = () => (
    <div className="min-h-svh bg-background">
        <header className="sticky top-0 z-10 border-b bg-background/80 backdrop-blur">
            <div className="flex h-14 items-center justify-between px-6 lg:px-10">
                <div className="flex items-center gap-2">
                    <div className="flex size-8 items-center justify-center rounded-lg bg-foreground text-background">
                        <RefreshCw className="size-4" />
                    </div>
                    <span className="text-lg font-semibold tracking-tight">AniSync</span>
                </div>
                <ModeToggle />
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
                                    ? "border-foreground text-foreground"
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

export default App;
