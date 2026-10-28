import { useCallback, useEffect, useState } from 'react';
import { api, getToken, setToken, clearToken, UserInfo } from './api';

interface AuthState {
  user: UserInfo | null;
  loading: boolean;
}

export function useAuth() {
  const [state, setState] = useState<AuthState>({ user: null, loading: !!getToken() });

  const loadMe = useCallback(async () => {
    if (!getToken()) { setState({ user: null, loading: false }); return; }
    try {
      const u = await api.get<UserInfo>('/api/auth/me');
      setState({ user: u, loading: false });
    } catch {
      clearToken();
      setState({ user: null, loading: false });
    }
  }, []);

  useEffect(() => { loadMe(); }, [loadMe]);

  const login = async (email: string, password: string) => {
    const r = await api.post<{ token: string; user: UserInfo }>('/api/auth/login', { email, password });
    setToken(r.token);
    setState({ user: r.user, loading: false });
  };

  const register = async (name: string, email: string, password: string) => {
    const r = await api.post<{ token: string; user: UserInfo }>('/api/auth/register', { name, email, password });
    setToken(r.token);
    setState({ user: r.user, loading: false });
  };

  const logout = () => { clearToken(); setState({ user: null, loading: false }); location.hash = '#/login'; };

  const yandexLogin = () => { location.href = '/api/auth/yandex/start'; };

  return { ...state, login, register, logout, yandexLogin, refresh: loadMe };
}
