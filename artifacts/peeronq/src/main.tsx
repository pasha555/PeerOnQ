import { createRoot } from 'react-dom/client';

import App from './App';
import { migrateLegacyStorage } from './compatibility/legacyBrandStorageMigration';

import './index.css';

migrateLegacyStorage();
createRoot(document.getElementById('root')!).render(<App />);
