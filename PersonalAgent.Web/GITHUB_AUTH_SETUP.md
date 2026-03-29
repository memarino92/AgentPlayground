# GitHub OAuth Setup for PersonalAgent

PersonalAgent uses GitHub OAuth 2.0 for authentication. This allows you to securely log in with your GitHub account on any device.

## Setup Instructions

### 1. Create a GitHub OAuth Application

1. Go to GitHub Settings → Developer settings → OAuth Apps
   - Link: https://github.com/settings/developers
2. Click "New OAuth App"
3. Fill in the form:
   - **Application name**: PersonalAgent (or your preference)
   - **Homepage URL**: `https://yourdomain.com` (or `http://localhost:5171` for local dev)
   - **Authorization callback URL**: `https://yourdomain.com/signin-github` (or `http://localhost:5171/signin-github` for local dev)
4. Click "Register application"
5. You'll see your **Client ID** and can generate a **Client Secret**

### 2. Set Environment Variables

#### For Local Development

Using `dotnet user-secrets`:

```bash
cd PersonalAgent.Web
dotnet user-secrets init
dotnet user-secrets set "Authentication:Schemes:GitHub:ClientId" "your-github-client-id"
dotnet user-secrets set "Authentication:Schemes:GitHub:ClientSecret" "your-github-client-secret"
```

#### Restrict Access to Specific Users

By default, any GitHub user can log in. To restrict access to only specific GitHub usernames:

```bash
# For a single user
dotnet user-secrets set "Authentication:Schemes:GitHub:AllowedUsers" "your-github-username"

# For multiple users (comma-separated)
dotnet user-secrets set "Authentication:Schemes:GitHub:AllowedUsers" "username1,username2,username3"
```

**Note**: AllowedUsers is case-insensitive and only checked if configured (if empty, all users are allowed).

#### For Production Deployment

Set these environment variables in your deployment environment:

```bash
GITHUB_CLIENT_ID=your-github-client-id
GITHUB_CLIENT_SECRET=your-github-client-secret
# Optional: restrict access to specific GitHub usernames
GITHUB_ALLOWED_USERS=username1,username2
```

Or if using Docker, set them as environment variables:

```dockerfile
ENV GITHUB_CLIENT_ID="your-github-client-id"
ENV GITHUB_CLIENT_SECRET="your-github-client-secret"
# Optional: restrict access to specific GitHub usernames
ENV GITHUB_ALLOWED_USERS="username1,username2"
```

### 3. Run the Application

```bash
cd PersonalAgent.Web
dotnet run
```

Visit `http://localhost:5171` and you should see a "Login with GitHub" button.

## How It Works

1. When you click "Login with GitHub", you're redirected to GitHub
2. GitHub authenticates you and asks for permission to access your email
3. You're redirected back to PersonalAgent with your user information
4. An authentication cookie is created
5. You can now access the chat interface
6. Click "Logout" to clear your authentication

## URLs

- **Login**: `/login`
- **Logout**: `/logout` (POST)
- **Callback**: `/signin-github` (handled automatically)

## Security Notes

- Credentials are stored securely using user-secrets for development
- Environment variables should be used for production
- The application uses secure cookies with HTTPS in production
- HTTPS is enforced in non-development environments
- Only your GitHub username, email, and profile URL are stored as claims

## Troubleshooting

**"Missing GITHUB_CLIENT_ID" error**

- Verify you've set the environment variables correctly
- Check that the configuration is being read properly
- For development, ensure `dotnet user-secrets` has been initialized

**Redirect URI mismatch error**

- Ensure the callback URL in your GitHub OAuth app matches exactly
- If running on a different port, update both the GitHub app settings and your local URL
- The callback should be `/signin-github`, not `/signin-oidc`

**"Invalid request" from GitHub**

- Check that the client ID and secret are correct
- Verify that your GitHub OAuth app is configured properly
- Check the Authorization callback URL in GitHub settings
