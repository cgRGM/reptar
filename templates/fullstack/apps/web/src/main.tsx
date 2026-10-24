import React from "react";
import ReactDOM from "react-dom/client";
import { BrowserRouter, Route, Routes } from "react-router";

import "./index.css";
import { IndexRoute } from "./routes/_index";
import { DashboardRoute } from "./routes/dashboard";
import { LoginRoute } from "./routes/login";
import { RootLayout } from "./routes/root";

const rootElement = document.querySelector("#root");

if (rootElement) {
  ReactDOM.createRoot(rootElement).render(
    <React.StrictMode>
      <BrowserRouter>
        <Routes>
          <Route path="/" element={<RootLayout />}>
            <Route index element={<IndexRoute />} />
            <Route path="login" element={<LoginRoute />} />
            <Route path="dashboard" element={<DashboardRoute />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </React.StrictMode>
  );
}
