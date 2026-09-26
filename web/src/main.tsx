import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import './styles.css';
import { App } from './app/App';

const root = document.getElementById('root');
if (!root) {
  throw new Error('No #root element');
}

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
