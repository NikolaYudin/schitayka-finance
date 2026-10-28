import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { setToken } from './api.ts'

// Приём токена после Яндекс-OAuth: бэкенд редиректит на /#/auth-success#token=JWT
// (два «хеша» — браузер склеивает в location.hash = "#/auth-success#token=...")
(function consumeOAuthToken() {
  const m = location.hash.match(/token=([^&]+)/);
  if (m) {
    setToken(decodeURIComponent(m[1]));
    history.replaceState(null, '', location.pathname + '#/');
  }
})();

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
