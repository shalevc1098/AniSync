# MyAnimeList API v2 Documentation

This document provides comprehensive guidance for working with the MyAnimeList (MAL) API v2.

## API Overview

- **Base URL**: `https://api.myanimelist.net/v2`
- **Authentication**: OAuth2 with PKCE
- **Response Format**: JSON (application/json; charset=UTF-8)
- **Request Format**: URL-form encoded for mutations (application/x-www-form-urlencoded)

## Authentication

### OAuth2 Flow with PKCE

1. **Generate PKCE Challenge**
   - Create a random code_verifier (43-128 characters)
   - Generate code_challenge = base64url(sha256(code_verifier))

2. **Authorization Request**
   ```
   https://myanimelist.net/v1/oauth2/authorize?
     response_type=code&
     client_id={client_id}&
     code_challenge={code_challenge}&
     state={state}&
     redirect_uri={redirect_uri}&
     code_challenge_method=S256
   ```

3. **Token Exchange**
   ```http
   POST https://myanimelist.net/v1/oauth2/token
   Content-Type: application/x-www-form-urlencoded
   
   client_id={client_id}&
   client_secret={client_secret}&
   grant_type=authorization_code&
   code={authorization_code}&
   redirect_uri={redirect_uri}&
   code_verifier={code_verifier}
   ```

4. **Token Refresh**
   ```http
   POST https://myanimelist.net/v1/oauth2/token
   Content-Type: application/x-www-form-urlencoded
   
   client_id={client_id}&
   client_secret={client_secret}&
   grant_type=refresh_token&
   refresh_token={refresh_token}
   ```

### Required Headers

All API requests must include:
- `X-MAL-Client-ID: {client_id}`
- `Authorization: Bearer {access_token}`

## Core Anime Endpoints

### Search Anime
```http
GET /anime?q={query}&limit={limit}&offset={offset}&fields={fields}&nsfw={boolean}
```

**Parameters:**
- `q`: Search query string
- `limit`: Number of results (max 100)
- `offset`: Pagination offset
- `fields`: Comma-separated list of fields to return
- `nsfw`: Include NSFW content (true/false)

### Get Anime Details
```http
GET /anime/{anime_id}?fields={fields}
```

**Available Fields:**
- Basic: `id`, `title`, `main_picture`
- Details: `alternative_titles`, `start_date`, `end_date`, `synopsis`, `mean`, `rank`
- Media: `media_type`, `status`, `genres`, `num_episodes`, `start_season`, `broadcast`
- Statistics: `num_list_users`, `num_scoring_users`, `nsfw`, `popularity`
- Related: `related_anime`, `related_manga`, `recommendations`, `studios`
- Pictures: `pictures`, `background`
- User Data: `my_list_status`

### Get User Anime List
```http
GET /users/{user_name}/animelist?status={status}&sort={sort}&limit={limit}&offset={offset}&fields={fields}&nsfw={boolean}
```

**Status Values:**
- `watching`
- `completed`
- `on_hold`
- `dropped`
- `plan_to_watch`

**Sort Options:**
- `list_score`
- `list_updated_at`
- `anime_title`
- `anime_start_date`
- `anime_id`

## Managing User List

### Get Anime List Status
```http
GET /anime/{anime_id}/my_list_status?fields={fields}
```

### Update Anime Status
```http
PATCH /anime/{anime_id}/my_list_status
Content-Type: application/x-www-form-urlencoded

status={status}&
is_rewatching={boolean}&
score={0-10}&
num_watched_episodes={number}&
priority={0-2}&
num_times_rewatched={number}&
rewatch_value={0-5}&
tags={comma_separated_tags}&
comments={text}&
start_date={yyyy-mm-dd}&
finish_date={yyyy-mm-dd}
```

**Status Values:**
- `watching`
- `completed`
- `on_hold`
- `dropped`
- `plan_to_watch`

### Delete from List
```http
DELETE /anime/{anime_id}/my_list_status
```

## Response Formats

### Success Response
```json
{
  "data": {
    // Response data
  },
  "paging": {
    "next": "https://api.myanimelist.net/v2/anime?offset=10&limit=10"
  }
}
```

### Error Response
```json
{
  "error": "invalid_token",
  "message": "The access token expired"
}
```

## Rate Limiting

- API has rate limits but exact values are not publicly documented
- Implement exponential backoff for 429 responses
- Cache responses where appropriate

## Common Patterns

### Batch Field Requests
Instead of making multiple requests, request all needed fields at once:
```
fields=id,title,main_picture,alternative_titles,start_date,end_date,synopsis,mean,rank,popularity,num_list_users,num_scoring_users,nsfw,genres,media_type,status,num_episodes,start_season,broadcast,source,average_episode_duration,rating,studios,my_list_status
```

### Pagination Handling
```javascript
let allResults = [];
let nextUrl = initialUrl;

while (nextUrl) {
  const response = await fetch(nextUrl);
  const data = await response.json();
  allResults = allResults.concat(data.data);
  nextUrl = data.paging?.next;
}
```

## Error Handling

| Status Code | Meaning |
|------------|---------|
| 200 | Success |
| 400 | Bad Request - Invalid parameters |
| 401 | Unauthorized - Invalid/missing token |
| 403 | Forbidden - Insufficient permissions |
| 404 | Not Found - Resource doesn't exist |
| 429 | Too Many Requests - Rate limited |
| 500 | Internal Server Error |

## Important Notes

- Alternative ID: Some anime may have an `alternative_id` that should be used instead of the regular ID
- NSFW Content: By default, NSFW content is excluded. Set `nsfw=true` to include
- Fields Parameter: Not all fields are returned by default. Always specify needed fields
- Token Expiry: Access tokens expire after 1 month, refresh tokens don't expire but can be revoked
- Client Credentials: Never expose client_secret in client-side code

## Useful Resources

- Unofficial API Specification: https://github.com/SuperMarcus/myanimelist-api-specification
- Jikan (Alternative API): https://jikan.moe/
- Node.js Wrapper: @chris-kode/myanimelist-api-v2